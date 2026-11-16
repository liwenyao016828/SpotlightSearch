using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SpotlightSearch.Interop;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search;

/// <summary>
/// Port of Services/Spotlight/SpotlightLauncher.cs, minus the native helpers.
/// Every target is re-validated right before it is used, so a stale history entry
/// pointing at a deleted file simply does nothing.
/// </summary>
internal static class Launcher
{
    public static bool TryLaunch(SpotlightSearchItem item)
    {
        if (!IsValidTarget(item)) return false;

        try
        {
            if (item.Kind == SpotlightResultKind.SystemAction) return RunSystemAction(item.Target);

            using var process = Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryReveal(SpotlightSearchItem item)
    {
        if (!CanReveal(item)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { "/select,", item.Target }
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryLaunchElevated(SpotlightSearchItem item, out bool cancelled)
    {
        cancelled = false;
        if (!CanLaunchElevated(item)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(item.Target)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user declined the UAC prompt: keep the window open, record nothing.
            cancelled = true;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool CanReveal(SpotlightSearchItem item) => IsValidTarget(item) && item.Kind switch
    {
        SpotlightResultKind.File or SpotlightResultKind.Folder => true,
        SpotlightResultKind.Application => File.Exists(item.Target),
        _ => false
    };

    public static bool CanLaunchElevated(SpotlightSearchItem item) =>
        item.Kind is SpotlightResultKind.Application or SpotlightResultKind.File
        && IsValidTarget(item)
        && File.Exists(item.Target);

    public static string? GetCopyableText(SpotlightSearchItem item) =>
        item.Kind == SpotlightResultKind.Calculation
            ? string.IsNullOrWhiteSpace(item.Target) ? null : item.Target
            : CanReveal(item) ? item.Target : null;

    public static bool IsValidTarget(SpotlightSearchItem item)
    {
        string target = item.Target;
        if (string.IsNullOrWhiteSpace(target) || FileVisibility.IsUncPath(target)) return false;
        if (target.Any(char.IsControl)) return false;

        return item.Kind switch
        {
            SpotlightResultKind.Settings or SpotlightResultKind.SystemAction => SystemCatalog.IsKnownTarget(item),
            SpotlightResultKind.Application => target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                                               || File.Exists(target)
                                               || Directory.Exists(target),
            SpotlightResultKind.File => File.Exists(target),
            SpotlightResultKind.Folder => Directory.Exists(target),
            // Calculations are copied, never process-launched.
            _ => false
        };
    }

    private static bool RunSystemAction(string target)
    {
        try
        {
            switch (target)
            {
                case "spotlight-action:lock":
                    return NativeMethods.LockWorkStation();

                case "shell:RecycleBinFolder":
                    Process.Start(new ProcessStartInfo("explorer.exe")
                    {
                        UseShellExecute = true,
                        ArgumentList = { "shell:RecycleBinFolder" }
                    })?.Dispose();
                    return true;

                case "spotlight-action:restart":
                    return RunShutdown("/r", "/t", "0");
                case "spotlight-action:shutdown":
                    return RunShutdown("/s", "/t", "0");
                case "spotlight-action:signOut":
                    return RunShutdown("/l");
                case "spotlight-action:hibernate":
                    return RunShutdown("/h");
                case "spotlight-action:advancedRestart":
                    return RunShutdown("/r", "/o", "/t", "0");
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Never passes /f: a non-zero timeout implicitly force-closes apps, and Windows
    /// should be the one to block on unsaved work.
    /// </summary>
    private static bool RunShutdown(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        return process != null;
    }
}
