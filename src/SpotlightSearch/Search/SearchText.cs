using System.Globalization;
using System.Text;

namespace SpotlightSearch.Search;

/// <summary>
/// Port of the original native text matcher (Native/Services/SettingsSearchMatcher.cpp
/// + vnotch_common.h normalize_search).
///
/// Normalization: lowercase, NFKD, drop combining marks, keep letters/digits,
/// collapse every other run of characters into a single space. This is what makes
/// "Café" match "cafe" and "C:\Program Files" match "program files".
///
/// Scoring is intentionally word-based instead of fuzzy-only: an exact word beats a
/// prefix, a prefix beats a mid-word hit, and the position of the matched word adds
/// a small bonus. The absolute numbers are the original constants — other parts of
/// the ranker (executable +300, folder +50, usage boost, pin +150) are tuned against them.
/// </summary>
internal static class SearchText
{
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        if (IsAscii(input))
        {
            var ascii = new StringBuilder(input.Length);
            bool previousWasSeparator = true;
            foreach (char ch in input)
            {
                char lower = (ch >= 'A' && ch <= 'Z') ? (char)(ch + 32) : ch;
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
                {
                    ascii.Append(lower);
                    previousWasSeparator = false;
                }
                else if (!previousWasSeparator)
                {
                    ascii.Append(' ');
                    previousWasSeparator = true;
                }
            }
            if (ascii.Length > 0 && ascii[^1] == ' ') ascii.Length--;
            return ascii.ToString();
        }

        string decomposed = input.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        bool separator = true;
        foreach (char ch in decomposed)
        {
            if (IsCombiningMark(ch)) continue;
            char normalized = ch is '\u0111' or '\u0110' ? 'd' : char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(normalized))
            {
                builder.Append(normalized);
                separator = false;
            }
            else if (!separator)
            {
                builder.Append(' ');
                separator = true;
            }
        }
        if (builder.Length > 0 && builder[^1] == ' ') builder.Length--;
        return builder.ToString();
    }

    public static bool IsMatch(string source, string query) =>
        GetNormalizedScore(Normalize(source), Normalize(query), allowFuzzy: true) > 0;

    /// <summary>Word-level score for already-normalized strings. 0 means "no match".</summary>
    public static int GetNormalizedScore(string normalizedSource, string normalizedQuery, bool allowFuzzy)
    {
        if (normalizedSource.Length == 0 || normalizedQuery.Length == 0) return 0;

        string[] queryWords = SplitWords(normalizedQuery);
        if (queryWords.Length == 0) return 0;

        string[] sourceWords = SplitWords(normalizedSource);
        if (sourceWords.Length == 0) return 0;

        int weakest = 1000;
        int total = 0;
        int exactMatches = 0;
        int fuzzyMatches = 0;

        foreach (string queryWord in queryWords)
        {
            int wordScore = WordMatchScore(sourceWords, queryWord, allowFuzzy, out bool isFuzzy);
            if (wordScore == 0 || (isFuzzy && ++fuzzyMatches > 1)) return 0;
            if (wordScore >= 450) exactMatches++;
            weakest = Math.Min(weakest, wordScore);
            total += wordScore;
        }

        int average = total / queryWords.Length;
        int phraseBonus = queryWords.Length > 1
                          && normalizedSource.Contains(normalizedQuery, StringComparison.Ordinal)
            ? 95
            : 0;
        int exactBonus = 60 * exactMatches / queryWords.Length;
        return weakest + average / 2 + exactBonus + phraseBonus;
    }

    private static int WordMatchScore(string[] sourceWords, string queryWord, bool allowFuzzy, out bool isFuzzy)
    {
        isFuzzy = false;
        int best = 0;
        bool unsegmented = IsUnsegmentedScript(queryWord);

        for (int index = 0; index < sourceWords.Length; index++)
        {
            string word = sourceWords[index];
            if (word.Length == 0) continue;

            int positionBonus = index switch { 0 => 45, 1 => 30, 2 => 15, _ => 0 };

            if (string.Equals(word, queryWord, StringComparison.Ordinal))
            {
                best = Math.Max(best, 460 + positionBonus);
                continue;
            }

            if (unsegmented && word.Contains(queryWord, StringComparison.Ordinal))
            {
                best = Math.Max(best, 300 + positionBonus);
                continue;
            }

            if (word.StartsWith(queryWord, StringComparison.Ordinal))
            {
                int coverage = queryWord.Length * 70 / Math.Max(1, word.Length);
                best = Math.Max(best, 320 + coverage + positionBonus);
                continue;
            }

            if (queryWord.Length >= 3 && word.Contains(queryWord, StringComparison.Ordinal))
            {
                int coverage = queryWord.Length * 45 / Math.Max(1, word.Length);
                best = Math.Max(best, 230 + coverage + positionBonus / 2);
            }
        }

        if (best > 0) return best;

        if (MatchesAcronym(sourceWords, queryWord)) return 210;

        if (!allowFuzzy || queryWord.Length < 4) return 0;

        foreach (string word in sourceWords)
        {
            if (word.Length < 4 || queryWord[0] != word[0]) continue;

            int lengthDelta = Math.Abs(queryWord.Length - word.Length);
            if (lengthDelta <= 1 && (Levenshtein(queryWord, word) == 1 || IsSingleTransposition(queryWord, word)))
            {
                isFuzzy = true;
                return 120;
            }

            if (queryWord.Length >= 5 && word.Length > queryWord.Length)
            {
                string prefix = word[..queryWord.Length];
                if (Levenshtein(queryWord, prefix) == 1 || IsSingleTransposition(queryWord, prefix))
                {
                    isFuzzy = true;
                    return 110;
                }
            }
        }

        return 0;
    }

    private static bool MatchesAcronym(string[] sourceWords, string queryWord)
    {
        if (queryWord.Length is < 2 or > 5 || sourceWords.Length < queryWord.Length) return false;

        for (int start = 0; start + queryWord.Length <= sourceWords.Length; start++)
        {
            bool all = true;
            for (int offset = 0; offset < queryWord.Length; offset++)
            {
                string word = sourceWords[start + offset];
                if (word.Length == 0 || word[0] != queryWord[offset])
                {
                    all = false;
                    break;
                }
            }
            if (all) return true;
        }
        return false;
    }

    private static int Levenshtein(string source, string target)
    {
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;
        if (source.Length > 62 || target.Length > 62) return 99;

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];
        for (int column = 0; column <= target.Length; column++) previous[column] = column;

        for (int row = 0; row < source.Length; row++)
        {
            current[0] = row + 1;
            for (int column = 0; column < target.Length; column++)
            {
                int cost = source[row] == target[column] ? 0 : 1;
                current[column + 1] = Math.Min(
                    Math.Min(current[column] + 1, previous[column + 1] + 1),
                    previous[column] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private static bool IsSingleTransposition(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int first = -1, second = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (first < 0) first = i;
            else if (second < 0) second = i;
            else return false;
        }
        return first >= 0 && second == first + 1 && a[first] == b[second] && a[second] == b[first];
    }

    private static string[] SplitWords(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsAscii(string value)
    {
        foreach (char ch in value)
        {
            if (ch >= 0x80) return false;
        }
        return true;
    }

    private static bool IsCombiningMark(char ch) => char.GetUnicodeCategory(ch) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Scripts written without word separators — a substring hit is a real word hit there.</summary>
    private static bool IsUnsegmentedScript(string word)
    {
        if (word.Length == 0) return false;
        foreach (char ch in word)
        {
            bool inRange = ch is >= '\u3040' and <= '\u30FF'
                or >= '\u3400' and <= '\u4DBF'
                or >= '\u4E00' and <= '\u9FFF'
                or >= '\uF900' and <= '\uFAFF'
                or >= '\uAC00' and <= '\uD7AF';
            if (!inRange) return false;
        }
        return true;
    }
}
