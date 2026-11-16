using System.IO;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search;

/// <summary>
/// Port of Native/Services/Spotlight/SpotlightRanker.cpp.
///
/// The score is a lexical score plus kind-based adjustments. Absolute values matter:
/// the usage store adds up to ~475 and pins add 150, so an app hit must outrank a
/// loose file hit but a strong exact file name must still be able to win.
/// </summary>
internal static class Ranker
{
    private static readonly HashSet<string> PopularExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "wt", "wt.exe", "bash", "bash.exe", "wsl", "wsl.exe",
        "calc", "calc.exe", "notepad", "notepad.exe",
        "explorer", "explorer.exe", "taskmgr", "taskmgr.exe",
        "regedit", "regedit.exe", "control", "control.exe",
        "mstsc", "mstsc.exe", "mspaint", "mspaint.exe", "paint", "paint.exe",
        "snippingtool", "snippingtool.exe", "cleanmgr", "cleanmgr.exe",
        "dxdiag", "dxdiag.exe", "msconfig", "msconfig.exe",
        "resmon", "resmon.exe", "perfmon", "perfmon.exe",
        "services", "services.msc", "ncpa", "ncpa.cpl",
        "sysdm", "sysdm.cpl", "appwiz", "appwiz.cpl",
        "devmgmt", "devmgmt.msc", "diskmgmt", "diskmgmt.msc",
        "compmgmt", "compmgmt.msc", "eventvwr", "eventvwr.msc",
        "main.cpl", "powercfg.cpl", "firewall.cpl",
        "chrome", "chrome.exe", "msedge", "msedge.exe",
        "firefox", "firefox.exe", "brave", "brave.exe", "opera", "opera.exe",
        "code", "code.exe", "devenv", "devenv.exe",
        "rider64", "rider64.exe", "datagrip64", "datagrip64.exe",
        "pycharm64", "pycharm64.exe", "idea64", "idea64.exe",
        "discord", "discord.exe", "spotify", "spotify.exe",
        "vlc", "vlc.exe", "steam", "steam.exe",
        "everything", "everything.exe", "putty", "putty.exe",
        "winscp", "winscp.exe", "7z", "7z.exe", "7zfm", "7zfm.exe",
        "python", "python.exe", "node", "node.exe", "git", "git.exe",
        "docker", "docker.exe", "winword", "winword.exe",
        "excel", "excel.exe", "powerpnt", "powerpnt.exe"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".cmd", ".bat", ".msc", ".cpl", ".com", ".ps1", ".appref-ms", ".lnk"
    };

    private static readonly HashSet<string> AssetJunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dds", ".bmp", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".tga", ".tif", ".tiff", ".psd", ".ai",
        ".dll", ".sys", ".drv", ".ocx", ".ax", ".so", ".dylib", ".lib", ".a", ".o", ".obj", ".exp", ".ilk", ".pdb",
        ".dat", ".tmp", ".temp", ".log", ".cache", ".bak", ".chk", ".dmp", ".swp",
        ".meta", ".asset", ".mat", ".prefab", ".unity", ".nfo", ".manifest"
    };

    public static double Score(SpotlightSearchItem item, string query)
    {
        string normalizedQuery = SearchText.Normalize(query);
        if (normalizedQuery.Length == 0) return 0;

        string title = SearchText.Normalize(item.DisplayTitle);
        string subtitle = SearchText.Normalize(item.Subtitle);
        string titleWithoutExtension = SearchText.Normalize(StripExtension(item.DisplayTitle));

        double score = LexicalScore(title, subtitle, normalizedQuery);

        if (item.Kind is SpotlightResultKind.Application or SpotlightResultKind.File
            && Path.IsPathFullyQualified(item.Target))
        {
            score = Math.Max(score,
                LexicalScore(SearchText.Normalize(GetFileName(item.Target)), string.Empty, normalizedQuery));
        }

        if (titleWithoutExtension.Length > 0 && titleWithoutExtension != title)
        {
            score = Math.Max(score, LexicalScore(titleWithoutExtension, string.Empty, normalizedQuery));
        }

        // Pinyin and Flow-style fuzzy are the "no literal hit, but it is clearly
        // meant" tiers: they only run when nothing stronger already matched.
        if (score < 700)
        {
            score = Math.Max(score, PinyinScore(title, normalizedQuery));
            score = Math.Max(score, FuzzyScore(item.DisplayTitle, query));
        }

        if (score <= 0) return 0;

        if (IsExecutableOrApp(item))
        {
            score += 300;
            if (IsPopularExecutable(item)) score += 100;
        }
        else if (item.Kind == SpotlightResultKind.Folder)
        {
            score += 50;
        }
        else if (IsAssetJunk(item))
        {
            score = Math.Max(10, score - 300);
        }

        return score;
    }

    private static double LexicalScore(string title, string subtitle, string query)
    {
        if (title.Length == 0) return 0;

        if (string.Equals(title, query, StringComparison.Ordinal)) return 1000;

        if (title.StartsWith(query, StringComparison.Ordinal))
            return 900 - Math.Min(100, title.Length - query.Length);

        double wordScore = MatchWordBoundaries(title, query);
        if (wordScore > 0) return wordScore;

        if (title.Contains(query, StringComparison.Ordinal)) return 700;
        if (SearchText.GetNormalizedScore(title, query, allowFuzzy: true) > 0) return 460;
        if (subtitle.Length > 0 && subtitle.Contains(query, StringComparison.Ordinal)) return 350;
        if (subtitle.Length > 0 && SearchText.GetNormalizedScore(subtitle, query, allowFuzzy: true) > 0) return 250;
        return 0;
    }

    // ---- pinyin tier (WinShun's reading model) -------------------------------

    [ThreadStatic] private static string? _pinyinQuery;
    [ThreadStatic] private static Pinyin.Matcher? _pinyinAll;
    [ThreadStatic] private static Pinyin.Matcher? _pinyinCommon;

    /// <summary>
    /// "ndbg" finds 年度报告. Mirrors WinShun's Query.cpp pinyin bonuses: a common
    /// reading beats a rare one (行 as xing over hang), word start beats the middle,
    /// and covering the whole name is its own reward.
    /// </summary>
    internal static double PinyinScore(string normalizedTitle, string normalizedQuery)
    {
        if (normalizedTitle.Length == 0 || !Pinyin.HasHan(normalizedTitle)) return 0;
        if (!Pinyin.IsPinyinTerm(normalizedQuery)) return 0;

        if (!ReferenceEquals(_pinyinQuery, normalizedQuery))
        {
            _pinyinQuery = normalizedQuery;
            _pinyinAll = new Pinyin.Matcher(normalizedQuery);
            _pinyinCommon = new Pinyin.Matcher(normalizedQuery, commonOnly: true);
        }

        var match = _pinyinAll!.Find(normalizedTitle);
        if (match is null) return 0;

        int bonus = -12;
        if (_pinyinCommon!.Find(normalizedTitle) is { } common)
        {
            match = common;
            bonus = 0;
        }

        bonus += match.Value.Start == 0 ? 26
            : IsWordStart(normalizedTitle, match.Value.Start) ? 16
            : 4;

        if (match.Value.Start == 0 && match.Value.Length >= normalizedTitle.Length)
            bonus += 40; // the whole name as pinyin

        return 560 + bonus;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || text[index - 1] == ' ';

    // ---- fuzzy tier (Flow.Launcher's matcher) --------------------------------

    /// <summary>"vsc" finds "Visual Studio Code"; scattered letters with a compact span score highest.</summary>
    private static double FuzzyScore(string title, string query)
    {
        if (title.Length == 0 || query.Length == 0) return 0;
        var result = FuzzyMatch.Match(query, title);
        // Flow's QuerySearchPrecision gate: a bare subsequence ("ab" in "alphabet")
        // must still clear the default 50 before it counts as a match at all.
        return result.Matched && result.Score >= 50 ? 480 + Math.Min(100, result.Score) / 2 : 0;
    }

    private static double MatchWordBoundaries(string title, string query)
    {
        bool prefixMatch = false;
        foreach (string word in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, query, StringComparison.Ordinal)) return 850;
            if (!prefixMatch && word.StartsWith(query, StringComparison.Ordinal)) prefixMatch = true;
        }
        return prefixMatch ? 800 : 0;
    }

    private static bool IsExecutableOrApp(SpotlightSearchItem item)
    {
        if (item.Kind == SpotlightResultKind.Application) return true;
        string extension = GetExtension(item.Title);
        if (extension.Length == 0) extension = GetExtension(item.Target);
        return ExecutableExtensions.Contains(extension);
    }

    private static bool IsAssetJunk(SpotlightSearchItem item)
    {
        string extension = GetExtension(item.Title);
        if (extension.Length == 0) extension = GetExtension(item.Target);
        return AssetJunkExtensions.Contains(extension);
    }

    private static bool IsPopularExecutable(SpotlightSearchItem item)
    {
        if (PopularExecutables.Contains(item.Title)) return true;
        if (PopularExecutables.Contains(StripExtension(item.Title))) return true;
        if (item.Target.Length == 0) return false;

        string targetName = GetFileName(item.Target);
        return PopularExecutables.Contains(targetName) || PopularExecutables.Contains(StripExtension(targetName));
    }

    private static string StripExtension(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        int dot = title.LastIndexOf('.');
        return dot <= 0 ? title : title[..dot];
    }

    private static string GetFileName(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/', ':']);
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string GetExtension(string path)
    {
        for (int i = path.Length - 1; i >= 0; i--)
        {
            char ch = path[i];
            if (ch == '.') return i == path.Length - 1 ? string.Empty : path[i..];
            if (ch is '\\' or '/' or ':') break;
        }
        return string.Empty;
    }
}
