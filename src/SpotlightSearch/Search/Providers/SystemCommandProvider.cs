using System.IO;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search.Providers;

/// <summary>
/// Port of Native/Services/Spotlight/Providers/SystemCommandProvider.cpp, widened with
/// the full Windows Settings index (WindowsSettingsCatalog): typing "代理" offers
/// ms-settings:network-proxy the way WinShun does.
///
/// The score bands (1450 exact / 1250 prefix / 1100 whole word / 850 normalized) sit
/// above app hits so typing "shutdown" always offers the power action first.
/// </summary>
internal sealed class SystemCommandProvider : ISearchProvider
{
    public bool IsInstant => true;

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        string normalizedQuery = SearchText.Normalize(query);
        if (normalizedQuery.Length == 0 || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>([]);

        var results = new List<SpotlightSearchItem>();
        foreach (var entry in SystemCatalog.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = entry.CreateItem();
            string fileName = entry.Kind == SpotlightResultKind.Application
                ? Path.GetFileName(entry.Target)
                : string.Empty;

            double score = ScoreEntry(entry.Keywords, item.Title, fileName, normalizedQuery);
            // Curated entries win ties against the raw index: "卸载" should offer the
            // apps page, not whichever alphabetically-first indexed task also has
            // the word as a keyword.
            if (score > 0 && Launcher.IsValidTarget(item)) results.Add(item with { Score = score + 5 });
        }

        // The full Settings index deepens the fixed catalog once its background walk
        // has finished; until then (or if the files are missing) only the fixed
        // catalog answers, so startup stays instant.
        foreach (var entry in WindowsSettingsCatalog.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Terms and title arrived pre-normalized from the catalog loader.
            double score = ScorePrecomputed(entry, normalizedQuery);
            if (score <= 0) continue;

            // The fixed catalog already covers a page: it keeps its (curated) entry.
            if (results.Any(item => item.Target.Equals(entry.MsSettings, StringComparison.OrdinalIgnoreCase))) continue;

            results.Add(new SpotlightSearchItem(
                $"system:winsettings:{entry.MsSettings}",
                SpotlightResultKind.Settings,
                entry.Title,
                "Windows 设置",
                entry.MsSettings) with { Score = score });
        }

        IReadOnlyList<SpotlightSearchItem> ordered = results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();

        return Task.FromResult(ordered);
    }

    /// <summary>
    /// Scores a Windows-settings entry whose keywords and title were normalized once
    /// at load time — no NFKD work per query (that was the hot path: ~17k normalizations
    /// per keystroke across the whole index).
    /// </summary>
    private static double ScorePrecomputed(WindowsSettingsCatalog.Entry entry, string normalizedQuery)
    {
        double best = 0;
        foreach (string term in entry.NormalizedTerms)
            best = Math.Max(best, ScoreNormalizedTerm(term, normalizedQuery));

        if (entry.NormalizedTitle.Length > 0)
            best = Math.Max(best, ScoreNormalizedTerm(entry.NormalizedTitle, normalizedQuery));

        if (best > 0 && entry.NormalizedTitle.Length > 0 && entry.NormalizedTitle == normalizedQuery)
            best += 100;

        return best;
    }

    private static double ScoreEntry(string keywords, string title, string fileName, string normalizedQuery)
    {
        double best = 0;

        foreach (string term in keywords.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            best = Math.Max(best, ScoreNormalizedTerm(SearchText.Normalize(term), normalizedQuery));
        }

        if (title.Length > 0)
        {
            best = Math.Max(best, ScoreNormalizedTerm(SearchText.Normalize(title), normalizedQuery));
        }

        if (fileName.Length > 0)
        {
            best = Math.Max(best, ScoreNormalizedTerm(SearchText.Normalize(fileName), normalizedQuery));
        }

        // A page titled exactly what was typed ("蓝牙" → the 蓝牙 page) is what was
        // meant; the full Settings index knows dozens of tasks that merely mention
        // the word, and they must not bury it.
        if (best > 0 && title.Length > 0 && SearchText.Normalize(title) == normalizedQuery)
            best += 100;

        return best;
    }

    private static double ScoreNormalizedTerm(string term, string query)
    {
        if (term.Length == 0 || query.Length == 0) return 0;

        if (string.Equals(term, query, StringComparison.Ordinal)) return 1450;

        if (term.StartsWith(query, StringComparison.Ordinal))
            return 1250 - Math.Min(100, term.Length - query.Length);

        if (ContainsWholeWord(term, query)) return 1100;

        if (SearchText.GetNormalizedScore(term, query, allowFuzzy: false) > 0) return 850;

        // Chinese aliases ("回收站") reachable by pinyin initials ("hsz").
        double pinyin = Ranker.PinyinScore(term, query);
        return pinyin > 0 ? pinyin + 300 : 0;
    }

    private static bool ContainsWholeWord(string term, string word)
    {
        int index = 0;
        while (index < term.Length)
        {
            int next = term.IndexOf(' ', index);
            string token = next < 0 ? term[index..] : term[index..next];
            if (string.Equals(token, word, StringComparison.Ordinal)) return true;
            if (next < 0) break;
            index = next + 1;
        }
        return false;
    }
}
