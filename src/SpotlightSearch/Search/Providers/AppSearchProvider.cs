using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search.Providers;

/// <summary>
/// Port of Services/Spotlight/Providers/AppSearchProvider.cs.
///
/// Index = Start Menu shortcuts + shell:AppsFolder (which covers Store/UWP apps that
/// have no .lnk on disk). Rebuilt lazily and cached for 30 minutes; the first search
/// after startup pays for it, every later one is a pure in-memory scan.
/// </summary>
internal sealed class AppSearchProvider : ISearchProvider
{
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private Task<IReadOnlyList<SpotlightSearchItem>>? _index;
    private DateTime _expiresUtc;

    public bool IsInstant => true;

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var apps = await WarmupAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || apps.Count == 0) return [];

        var matches = new List<SpotlightSearchItem>();
        foreach (var app in apps)
        {
            double score = Ranker.Score(app, query);
            if (score > 0) matches.Add(app with { Score = score });
        }

        if (matches.Count == 0) return [];

        matches.Sort(CompareScoreAndTitle);
        return matches.Count <= limit ? matches : matches.GetRange(0, limit);
    }

    public Task<IReadOnlyList<SpotlightSearchItem>> WarmupAsync()
    {
        lock (_gate)
        {
            if (_index == null || (_index.IsCompleted && (DateTime.UtcNow >= _expiresUtc || _index.IsFaulted)))
            {
                _index = Task.Run<IReadOnlyList<SpotlightSearchItem>>(BuildIndex);
                _expiresUtc = DateTime.UtcNow.Add(IndexLifetime);
            }
            return _index;
        }
    }

    private static int CompareScoreAndTitle(SpotlightSearchItem a, SpotlightSearchItem b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0) return byScore;
        int byTitle = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        return byTitle != 0 ? byTitle : string.CompareOrdinal(a.Id, b.Id);
    }

    private static IReadOnlyList<SpotlightSearchItem> BuildIndex()
    {
        var apps = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
        AddStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        AddAppsFolderItems(apps);

        // Icons stay undecoded here on purpose: only the rows that actually get
        // displayed ever ask the shell for a bitmap.
        return apps.Values.ToArray();
    }

    private static void AddStartMenuShortcuts(IDictionary<string, SpotlightSearchItem> apps, string startMenu)
    {
        if (string.IsNullOrWhiteSpace(startMenu)) return;

        string programs = Path.Combine(startMenu.TrimEnd('\\', '/'), "Programs");
        if (!Directory.Exists(programs)) return;

        var queue = new Queue<(DirectoryInfo Directory, int Depth)>();
        queue.Enqueue((new DirectoryInfo(programs), 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception)
            {
                // Access denied or a vanished directory: skip the subtree.
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                if (entry is DirectoryInfo subdirectory)
                {
                    if (depth < 32) queue.Enqueue((subdirectory, depth + 1));
                    continue;
                }

                if (!entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;

                string title = entry.Name[..^4];
                if (title.Length == 0) continue;

                AddIfMissing(apps, new SpotlightSearchItem(
                    $"app:{entry.FullName}", SpotlightResultKind.Application, title,
                    "应用程序", entry.FullName, entry.FullName));
            }
        }
    }

    private static void AddAppsFolderItems(IDictionary<string, SpotlightSearchItem> apps)
    {
        object? shellObject = null;
        object? folderObject = null;
        object? itemsObject = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;

            shellObject = Activator.CreateInstance(shellType);
            if (shellObject == null) return;

            dynamic shell = shellObject;
            folderObject = shell.NameSpace("shell:AppsFolder");
            if (folderObject == null) return;

            dynamic folder = folderObject;
            itemsObject = folder.Items();
            if (itemsObject is not IEnumerable items) return;

            foreach (object itemObject in items)
            {
                try
                {
                    dynamic item = itemObject;
                    string name = ReadString(() => item.Name);
                    string path = ReadString(() => item.Path);
                    string appId = ReadString(() => item.ExtendedProperty("System.AppUserModel.ID"));
                    string target = ResolveTarget(path, appId);
                    if (name.Length == 0 || target.Length == 0) continue;

                    AddIfMissing(apps, new SpotlightSearchItem(
                        $"app:{target}", SpotlightResultKind.Application, name, "应用程序", target,
                        File.Exists(path) ? path : null));
                }
                catch (Exception)
                {
                    // One broken shell item must not discard the rest of the index.
                }
                finally
                {
                    ReleaseComObject(itemObject);
                }
            }
        }
        catch (Exception)
        {
            // shell:AppsFolder is optional; Start Menu shortcuts alone still work.
        }
        finally
        {
            ReleaseComObject(itemsObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellObject);
        }
    }

    /// <summary>
    /// Mirrors vn_app_search_provider_classify_target: a real path or an existing
    /// shell: target is used as-is, otherwise rebuild the AppsFolder moniker.
    /// </summary>
    private static string ResolveTarget(string path, string appId)
    {
        if (FileVisibility.IsUncPath(path)) return string.Empty;

        bool isShellTarget = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        bool exists = !isShellTarget && (File.Exists(path) || Directory.Exists(path));

        if (isShellTarget || exists) return path;
        if (appId.Length > 0) return $"shell:AppsFolder\\{appId}";
        if (path.Length > 0) return $"shell:AppsFolder\\{path}";
        return string.Empty;
    }

    private static string ReadString(Func<object?> getter)
    {
        try
        {
            return Convert.ToString(getter())?.Trim() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void AddIfMissing(IDictionary<string, SpotlightSearchItem> apps, SpotlightSearchItem item)
    {
        if (!apps.ContainsKey(item.Title)) apps[item.Title] = item;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch (Exception)
        {
            // Already released elsewhere.
        }
    }
}
