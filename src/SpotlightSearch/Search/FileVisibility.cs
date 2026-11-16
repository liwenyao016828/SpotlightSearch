using System.IO;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search;

/// <summary>
/// Port of Native/Services/Spotlight/SpotlightFileVisibility.cpp.
///
/// This is the "don't show the user their own plumbing" policy: filesystem discovery
/// never returns things under Windows/Program Files/AppData, never returns build or
/// cache directories, and never returns binary extensions. An explicitly typed path
/// always wins over these filters.
/// </summary>
internal static class FileVisibility
{
    private static readonly HashSet<string> HiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "$Recycle.Bin", "System Volume Information", ".git", ".svn", ".hg",
        ".vs", ".vscode", ".idea", ".cache", ".nuget", "node_modules", "__pycache__", "obj",
        "Cache", "Caches", "Temp"
    };

    public static readonly HashSet<string> HiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".sys", ".drv", ".ocx", ".pdb", ".obj", ".mui", ".cat",
        ".tmp", ".temp", ".cache", ".etl", ".manifest"
    };

    public static IReadOnlyList<string> HiddenRoots { get; } = BuildHiddenRoots();

    public static bool IsHiddenDirectory(string name) => HiddenDirectories.Contains(name);

    public static bool IsExplicitPath(string query) => IsPathFullyQualified(TrimQuotes(query));

    public static bool ShouldSearch(string query) => TrimQuotes(query).Length >= 2 || IsExplicitPath(query);

    public static bool IsFileNameQuery(string query)
    {
        string name = TrimQuotes(query);
        if (name.Length <= 2) return false;
        foreach (char ch in name)
        {
            if (ch is '\\' or '/' or '*' or '?' or ':' or '<' or '>' or '|') return false;
        }
        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length > 0 && Path.GetExtension(name).Length > 1;
    }

    public static bool IsUncPath(string path)
    {
        string value = path.Trim();
        if (value.Length < 2) return false;

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"file:\\", StringComparison.OrdinalIgnoreCase))
        {
            string rest = value[7..];
            if (rest.Length == 0 || rest[0] is '/' or '\\') return false;
            if (rest.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith(@"localhost\", StringComparison.OrdinalIgnoreCase)) return false;
            if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] is ':' or '|') return false;
            return true;
        }

        bool doubleBackslash = value[0] == '\\' && value[1] == '\\';
        bool doubleSlash = value[0] == '/' && value[1] == '/';
        if (!doubleBackslash && !doubleSlash) return false;

        if (value.Length >= 4 && value[0] == '\\' && value[1] == '\\'
            && value[2] is '?' or '.' && value[3] == '\\')
        {
            string rest = value[4..];
            return rest.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)
                   || rest.StartsWith("UNC/", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    public static bool ShouldInclude(SpotlightSearchItem item, string query)
    {
        string target = item.Target ?? string.Empty;
        if (IsUncPath(target)) return false;

        string path = target.IndexOf('/') >= 0 ? target.Replace('/', '\\') : target;

        if (IsExplicitPath(query))
        {
            string normalizedQuery = TrimQuotes(query).Replace('/', '\\');
            return path.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        }

        if (IsFileNameQuery(query)
            && string.Equals(Path.GetFileName(path), TrimQuotes(query), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string root in HiddenRoots)
        {
            if (IsSameOrBelow(path, root)) return false;
        }

        foreach (string segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (HiddenDirectories.Contains(segment)) return false;
        }

        if (item.Kind == SpotlightResultKind.Folder) return true;

        return !HiddenExtensions.Contains(Path.GetExtension(path));
    }

    public static bool IsSameOrBelow(string path, string root)
    {
        string trimmed = root.TrimEnd('\\');
        if (path.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > trimmed.Length
               && path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
               && path[trimmed.Length] == '\\';
    }

    public static bool IsPathFullyQualified(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            && path[2] is '\\' or '/') return true;
        return path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';
    }

    private static string TrimQuotes(string value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Trim('"').Trim();
    }

    private static IReadOnlyList<string> BuildHiddenRoots()
    {
        string[] candidates =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        ];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>(candidates.Length);
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string trimmed = Path.TrimEndingDirectorySeparator(candidate);
            if (seen.Add(trimmed)) roots.Add(trimmed);
        }
        return roots;
    }
}
