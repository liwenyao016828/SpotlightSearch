using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using SpotlightSearch.Interop;

namespace SpotlightSearch.Search;

/// <summary>
/// The full Windows Settings index, straight from the files Windows ships for its own
/// search: %WINDIR%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml. WinShun
/// reads the same files (src/core/SystemCatalog.cpp) — that is why typing "代理" there
/// jumps to ms-settings:network-proxy.
///
/// Each block names a page (older files: &lt;Node Type="Page"&gt;, newer: &lt;PageID&gt;)
/// plus PolicyIds, one of which is the page's ms-settings: name. Names and keywords are
/// indirect strings ("@{windows?ms-resource://...}") in the display language, resolved
/// through SHLoadIndirectString.
///
/// Entries from the security host are skipped (Windows Security opens via
/// windowsdefender:, not ms-settings:). Condition checks are left out: showing a page
/// that happens to be absent costs nothing, the Settings app itself copes.
///
/// Loaded once in the background; SystemCommandProvider scores whatever is ready, so
/// the fixed catalog answers instantly and these deepen it when the walk finishes.
/// </summary>
internal static class WindowsSettingsCatalog
{
    private const string SecurityHostId = "{6E6DDBCB-9C89-434B-A994-D5F22239523B}";

    /// <summary>
    /// NormalizedTerms / NormalizedTitle are computed once at load time. Searching
    /// over a thousand entries then spends zero time on NFKD normalization.
    /// </summary>
    internal sealed record Entry(string MsSettings, string Title)
    {
        public string NormalizedTitle { get; init; } = "";
        public IReadOnlyList<string> NormalizedTerms { get; init; } = [];
    }

    private static volatile IReadOnlyList<Entry> _entries = [];

    internal static IReadOnlyList<Entry> Entries => _entries;

    internal static bool IsLoaded => _entries.Count > 0;

    /// <summary>True when the index directory exists at all (it does on every desktop Windows).</summary>
    internal static bool IsAvailable()
    {
        try
        {
            return Directory.EnumerateFiles(SettingsIndexDirectory(), "AllSystemSettings_*.xml").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Parses the index on a worker thread. Fire and forget; results publish atomically.</summary>
    internal static void LoadInBackground()
    {
        if (_loadStarted) return;
        _loadStarted = true;

        Task.Run(() =>
        {
            try
            {
                _entries = Load();
            }
            catch (Exception)
            {
                // The fixed catalog still answers; nothing to report.
            }
        });
    }

    private static bool _loadStarted;

    private static IReadOnlyList<Entry> Load()
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(SettingsIndexDirectory(), "AllSystemSettings_*.xml")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }

        // Windows ships one index per edition; newer ones are larger. Parse all, prefer
        // entries from the largest first so their keywords win the dedupe.
        var indexes = new List<List<ParsedBlock>>();
        foreach (string file in files)
        {
            try
            {
                indexes.Add(ParseIndex(file));
            }
            catch (Exception)
            {
                // One unreadable file: the rest still cover the settings.
            }
        }
        if (indexes.Count == 0) return [];

        indexes.Sort((a, b) => b.Count.CompareTo(a.Count));

        // Page → ms-settings name, from every file that has PolicyIds (the newest layout).
        var pageUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes)
        {
            foreach (ParsedBlock block in index)
            {
                if (block.Page.Length == 0 || block.PolicyIds.Length == 0) continue;
                if (pageUris.ContainsKey(block.Page)) continue;

                string uri = ChoosePageUri(block.Page, block.PolicyIds);
                if (uri.Length > 0) pageUris[block.Page] = uri;
            }
        }

        var resolver = new IndirectStringResolver();
        var raw = new List<(string Command, string Description, IReadOnlyList<string> KeywordLists)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in indexes)
        {
            foreach (ParsedBlock block in index)
            {
                if (!seen.Add(block.Key)) continue;
                if (block.Host == SecurityHostId) continue;

                string command;
                if (block.DeepLink.Length > 0)
                {
                    command = CommandForDeepLink(block.DeepLink);
                }
                else if (block.Page.Length > 0 && pageUris.TryGetValue(block.Page, out string? uri))
                {
                    command = "ms-settings:" + uri;
                }
                else
                {
                    continue;
                }
                if (command.Length == 0) continue;

                raw.Add((command, block.Description, block.KeywordLists));
            }
        }

        // Resolving indirect strings dominates the load (~2500 lookups), and so does
        // normalizing every keyword. Both are independent per entry — run them in
        // parallel, then dedupe on one thread. Search-time normalization drops to zero.
        var resolved = new (string Title, string[] NormTerms, string NormTitle)[raw.Count];
        Parallel.For(0, raw.Count, i =>
        {
            string title = resolver.Resolve(raw[i].Description).Trim();
            var normWords = new List<string>();
            foreach (string list in raw[i].KeywordLists)
            foreach (string word in SplitKeywords(resolver.Resolve(list)))
            {
                string norm = SearchText.Normalize(word);
                if (norm.Length > 0) normWords.Add(norm);
            }
            resolved[i] = (title, normWords.ToArray(), SearchText.Normalize(title));
        });

        var entries = new List<Entry>();
        var byNameAndCommand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < raw.Count; i++)
        {
            string title = resolved[i].Title;
            if (title.Length == 0) continue;

            // Same task in two editions (each with its own keywords): one entry.
            string dedupeKey = title + '\n' + raw[i].Command.ToLowerInvariant();
            if (byNameAndCommand.TryGetValue(dedupeKey, out int existing))
            {
                Entry prior = entries[existing];
                entries[existing] = prior with
                {
                    NormalizedTerms = prior.NormalizedTerms.Concat(resolved[i].NormTerms).ToArray()
                };
                continue;
            }
            byNameAndCommand[dedupeKey] = entries.Count;
            entries.Add(new Entry(raw[i].Command, title)
            {
                NormalizedTitle = resolved[i].NormTitle,
                NormalizedTerms = resolved[i].NormTerms
            });
        }

        return entries;
    }

    private static string SettingsIndexDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "Settings");

    // ---- index parsing -------------------------------------------------------

    private sealed record ParsedBlock(
        string Key,
        string Page,
        string PolicyIds,
        string DeepLink,
        string Host,
        string Description,
        IReadOnlyList<string> KeywordLists);

    private static List<ParsedBlock> ParseIndex(string file)
    {
        var blocks = new List<ParsedBlock>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

        using XmlReader reader = XmlReader.Create(file, settings);
        string key = "", page = "", policyIds = "", deepLink = "", host = "", description = "";
        var keywordLists = new List<string>();
        bool inside = false;
        int paths = 0; // the page comes from the first <Path>

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Name == "SearchableContent")
                {
                    if (inside && key.Length > 0)
                        blocks.Add(new ParsedBlock(key, page, policyIds, deepLink, host, description, keywordLists.ToArray()));
                    key = page = policyIds = deepLink = host = description = "";
                    keywordLists = new List<string>();
                    inside = false;
                }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.Name)
            {
                case "SearchableContent":
                    key = page = policyIds = deepLink = host = description = "";
                    keywordLists = new List<string>();
                    inside = true;
                    paths = 0;
                    break;
                case "Path" when inside:
                    paths++;
                    break;
                case "Node" when inside && reader.GetAttribute("Type") == "Page" && paths <= 1 && page.Length == 0:
                    page = reader.ReadElementContentAsString().Trim();
                    break;
                case "Filename" when inside:
                    key = reader.ReadElementContentAsString().Trim();
                    break;
                case "DeepLink" when inside:
                    deepLink = reader.ReadElementContentAsString().Trim();
                    break;
                case "HostID" when inside:
                    host = reader.ReadElementContentAsString().Trim();
                    break;
                case "Description" when inside:
                    description = reader.ReadElementContentAsString().Trim();
                    break;
                case "PageID" when inside && paths <= 1 && page.Length == 0:
                    page = reader.ReadElementContentAsString().Trim();
                    break;
                case "PolicyIds" when inside && paths <= 1 && policyIds.Length == 0:
                    policyIds = reader.ReadElementContentAsString().Trim();
                    break;
                case "HighKeywords" or "Keywords" when inside:
                {
                    // "@@a.dll,-1@b.dll,-2": several lists in one string.
                    string text = reader.ReadElementContentAsString().Trim();
                    if (text.StartsWith("@@", StringComparison.Ordinal))
                    {
                        foreach (string part in text[2..].Split('@', StringSplitOptions.RemoveEmptyEntries))
                            keywordLists.Add("@" + part);
                    }
                    else if (text.Length > 0)
                    {
                        keywordLists.Add(text);
                    }
                    break;
                }
            }
        }

        return blocks;
    }

    /// <summary>
    /// The PolicyIds list mixes page names and parent categories; the page's own
    /// ms-settings: name is the id the page name contains (minus dashes), else the id
    /// the most others build on.
    /// </summary>
    private static string ChoosePageUri(string page, string policyIds)
    {
        var ids = new List<string>();
        foreach (string id in policyIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ids.Add(id);
        if (ids.Count == 0) return "";

        string pageName = page.ToLowerInvariant();
        string named = "";
        foreach (string id in ids)
        {
            string candidate = id;
            if (candidate.Length > named.Length && pageName.Contains(candidate.Replace("-", ""), StringComparison.Ordinal))
                named = candidate;
        }
        if (named.Length > 0) return named;

        string best = ids[0];
        int bestCount = 0;
        foreach (string id in ids)
        {
            int count = ids.Count(other => other.StartsWith(id + "-", StringComparison.Ordinal));
            if (count > bestCount)
            {
                best = id;
                bestCount = count;
            }
        }
        return best;
    }

    /// <summary>
    /// A DeepLink is either already runnable (%windir%\..., C:\..., a URI) or a Control
    /// Panel item by canonical name ("Microsoft.X" or "Microsoft.X\PageY") which turns
    /// into a control.exe /name invocation.
    /// </summary>
    private static string CommandForDeepLink(string deepLink)
    {
        if (deepLink.StartsWith('%') || deepLink.Contains(@":\") || Uri.IsWellFormedUriString(deepLink, UriKind.Absolute))
            return deepLink;

        int slash = deepLink.IndexOf('\\');
        if (slash <= 0 || !deepLink.StartsWith("Microsoft.", StringComparison.Ordinal)) return "";

        string command = $"%windir%\\system32\\control.exe /name {deepLink[..slash]}";
        if (slash + 1 < deepLink.Length)
            command += $" /page {deepLink[(slash + 1)..]}";
        return command;
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>"a; b；c": split at ASCII and full-width semicolons.</summary>
    internal static IEnumerable<string> SplitKeywords(string list)
    {
        int start = 0;
        for (int i = 0; i <= list.Length; i++)
        {
            if (i < list.Length && list[i] is not (';' or '；')) continue;
            string word = list[start..i].Trim();
            if (word.Length > 0) yield return word;
            start = i + 1;
        }
    }

    /// <summary>
    /// Indirect strings ("@shell32.dll,-24389", "@{windows?ms-resource://...}") in the
    /// display language, each looked up once.
    /// </summary>
    private sealed class IndirectStringResolver
    {
        // Guarded: the catalog resolves thousands of these in a Parallel.For, and an
        // unsynchronized Dictionary corrupts and hangs when two lookups race.
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

        public string Resolve(string s)
        {
            if (s.Length == 0 || s[0] != '@') return s;
            lock (_gate)
            {
                if (_cache.TryGetValue(s, out string? cached)) return cached;
            }

            string text = Load(s);
            // "@%SystemRoot%\System32\SyncCenter.dll,-3000#immutable1"
            if (text.Length == 0)
            {
                int hash = s.IndexOf('#');
                if (hash > 0) text = Load(s[..hash]);
            }
            lock (_gate)
            {
                _cache[s] = text;
            }
            return text;
        }

        private static string Load(string s)
        {
            try
            {
                var buffer = new StringBuilder(4096);
                return NativeMethods.SHLoadIndirectString(s, buffer, 4096, IntPtr.Zero) == 0
                    ? buffer.ToString()
                    : "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
