using System.IO;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search.Providers;

/// <summary>
/// Port of Native/Services/Spotlight/Providers/LocalFileSearchProvider.cpp.
///
/// The original walked volumes with FindFirstFileExW and a hard time budget. Same
/// policy here with managed enumeration: breadth-first across all ready local
/// volumes, one directory per volume per round so a huge C: scan cannot starve D:,
/// and a hard stop when the budget expires. Whatever was found by then is the answer.
///
/// The original prefers Everything's index when it is running; this build has no
/// index, so the walk is the only filesystem source.
/// </summary>
internal sealed class FileSearchProvider : ISearchProvider
{
    private const int MaxResults = 200;
    private const int MaxQueryLength = 256;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".cmd", ".bat", ".msc", ".cpl", ".com", ".ps1", ".appref-ms", ".lnk"
    };

    private readonly Func<Settings.AppSettings> _settings;

    public FileSearchProvider(Func<Settings.AppSettings> settings) => _settings = settings;

    public bool IsInstant => false;

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (!settings.FileSearchEnabled || limit <= 0 || !FileVisibility.ShouldSearch(query))
            return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>([]);

        int budget = settings.FileSearchBudgetMs;
        return Task.Run(() => SearchCore(query, limit, budget, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<SpotlightSearchItem> SearchCore(
        string rawQuery, int limit, int budgetMilliseconds, CancellationToken token)
    {
        string query = (rawQuery ?? string.Empty).Trim().Trim('"').Trim();
        if (query.Length < 2 || query.Length > MaxQueryLength) return [];
        query = query.Replace('/', '\\');
        if (query.StartsWith(@"\\", StringComparison.Ordinal)) return [];

        bool explicitPath = IsAbsoluteLocalPath(query);
        int capacity = Math.Clamp(limit, 1, MaxResults);
        string normalizedQuery = SearchText.Normalize(query);
        var results = new List<SpotlightSearchItem>();

        List<string> roots = ReadyLocalRoots();
        if (explicitPath)
        {
            string fullPath = SafeFullPath(query);
            if (fullPath.Length == 0) return [];
            query = fullPath;

            if (File.Exists(fullPath))
            {
                AddResult(results, CreateItem(fullPath, query), capacity);
                return results;
            }

            string parent = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (parent.Length == 0) return [];
            roots = [parent];
        }

        long deadline = Environment.TickCount64 + Math.Min(budgetMilliseconds, 5000);
        var queues = new List<Queue<DirectoryInfo>>(roots.Count);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            queues.Add(new Queue<DirectoryInfo>([new DirectoryInfo(root)]));
        }

        bool anyPending = true;
        while (anyPending && !token.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            anyPending = false;

            foreach (var queue in queues)
            {
                if (queue.Count == 0) continue;
                anyPending = true;

                DirectoryInfo directory = queue.Dequeue();
                if (FileVisibility.IsHiddenDirectory(directory.Name)) continue;

                List<FileSystemInfo> entries;
                try
                {
                    entries = directory.EnumerateFileSystemInfos().ToList();
                }
                catch (Exception)
                {
                    // Access denied, deleted drive, path too long: skip the subtree.
                    continue;
                }

                foreach (FileSystemInfo entry in entries)
                {
                    if (token.IsCancellationRequested || Environment.TickCount64 >= deadline) break;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    bool isFolder = entry is DirectoryInfo;
                    bool filtered = (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

                    if (isFolder)
                    {
                        if (filtered && !explicitPath) continue;
                        if (FileVisibility.IsHiddenDirectory(entry.Name)) continue;
                        if (IsUnderHiddenRoot(entry.FullName)) continue;
                        queue.Enqueue((DirectoryInfo)entry);
                        continue;
                    }

                    if (filtered && !explicitPath) continue;

                    bool matches = explicitPath
                        ? entry.FullName.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                        : SearchText.Normalize(entry.Name).Contains(normalizedQuery, StringComparison.Ordinal);

                    if (!matches) continue;

                    var item = CreateItem(entry.FullName, query);
                    if (item == null) continue;
                    AddResult(results, item, capacity);
                }
            }
        }

        results.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
        });

        return results.Count <= capacity ? results : results.GetRange(0, capacity);
    }

    private static SpotlightSearchItem? CreateItem(string path, string query)
    {
        bool isFolder = Directory.Exists(path);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0) name = path;

        SpotlightResultKind kind;
        string prefix;
        if (isFolder)
        {
            kind = SpotlightResultKind.Folder;
            prefix = "folder";
        }
        else if (ExecutableExtensions.Contains(Path.GetExtension(path)))
        {
            kind = SpotlightResultKind.Application;
            prefix = "app";
        }
        else
        {
            kind = SpotlightResultKind.File;
            prefix = "file";
        }

        var item = new SpotlightSearchItem($"{prefix}:{path}", kind, name, path, path, path);
        if (!FileVisibility.ShouldInclude(item, query)) return null;

        double score = Ranker.Score(item, query);
        return score <= 0 ? null : item with { Score = score };
    }

    private static void AddResult(List<SpotlightSearchItem> results, SpotlightSearchItem? item, int capacity)
    {
        if (item == null) return;
        if (results.Exists(existing => string.Equals(existing.Target, item.Target, StringComparison.OrdinalIgnoreCase)))
            return;

        results.Add(item);

        // Keep the working set bounded: a broad query can match thousands of paths
        // inside the time budget, but only the best few hundred can ever be shown.
        if (results.Count > capacity * 4)
        {
            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            results.RemoveRange(capacity * 2, results.Count - capacity * 2);
        }
    }

    private static bool IsUnderHiddenRoot(string path)
    {
        foreach (string root in FileVisibility.HiddenRoots)
        {
            if (FileVisibility.IsSameOrBelow(path, root)) return true;
        }
        return false;
    }

    private static List<string> ReadyLocalRoots()
    {
        var roots = new List<string>(4);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!drive.IsReady) continue;
                roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception)
            {
                // A drive that disappears mid-enumeration is not an error worth reporting.
            }
        }
        return roots;
    }

    private static bool IsAbsoluteLocalPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
