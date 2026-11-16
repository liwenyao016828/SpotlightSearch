namespace SpotlightSearch.Search;

/// <summary>
/// Port of Flow.Launcher's Infrastructure/StringMatcher.cs fuzzy search, the
/// algorithm that makes "vsc" find "Visual Studio Code". Two tracks run in one
/// pass over the candidate:
///
/// 1. Acronym: characters at index 0, after a space, uppercase, or digits are
///    acronym characters; every query character must land on one in order.
///    Digit runs count as one group ("vs19" is [v][s][19]).
/// 2. Fuzzy: the query is split on spaces and every substring must match as a
///    contiguous-ish run in the candidate; scores reward matches near the start
///    and compact matches.
///
/// This lives next to the exact/prefix/word tiers in Ranker: Flow's fuzzy is the
/// "scattered letters still find it" tier the word-based matcher cannot reach.
/// </summary>
internal static class FuzzyMatch
{
    public readonly record struct Result(bool Matched, int Score);

    public static Result Match(string query, string stringToCompare)
    {
        if (string.IsNullOrEmpty(stringToCompare) || string.IsNullOrEmpty(query))
            return new Result(false, 0);

        query = query.Trim();
        string compare = stringToCompare; // acronyms need the original case

        var acronymMatchData = new List<int>();
        int currentAcronymQueryIndex = 0;
        int acronymsTotalCount = 0;
        int acronymsMatched = 0;

        string queryToCompare = query.ToLowerInvariant();

        string[] querySubstrings = queryToCompare.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (querySubstrings.Length == 0) return new Result(false, 0);
        int currentQuerySubstringIndex = 0;
        string currentQuerySubstring = querySubstrings[0];
        int currentQuerySubstringCharacterIndex = 0;

        int firstMatchIndex = -1;
        int firstMatchIndexInWord = -1;
        int lastMatchIndex = 0;
        bool allQuerySubstringsMatched = false;
        bool matchFoundInPreviousLoop = false;
        bool allSubstringsContainedInCompareString = true;

        var indexList = new List<int>();
        var spaceIndices = new List<int>();

        for (int compareStringIndex = 0; compareStringIndex < compare.Length; compareStringIndex++)
        {
            // If acronym matching finished, count the remaining acronyms for scoring.
            if (currentAcronymQueryIndex >= query.Length && acronymsMatched == query.Length)
            {
                if (IsAcronymCount(compare, compareStringIndex)) acronymsTotalCount++;
                continue;
            }

            if (currentAcronymQueryIndex >= query.Length) break;

            char originalChar = compare[compareStringIndex];
            char compareChar = char.ToLowerInvariant(originalChar);

            if (compareChar == ' ' && currentQuerySubstringIndex == 0)
                spaceIndices.Add(compareStringIndex);

            if (IsAcronym(compare, compareStringIndex))
            {
                if (compareChar == queryToCompare[currentAcronymQueryIndex])
                {
                    acronymMatchData.Add(compareStringIndex);
                    acronymsMatched++;
                    currentAcronymQueryIndex++;
                }
            }

            if (IsAcronymCount(compare, compareStringIndex)) acronymsTotalCount++;

            if (allQuerySubstringsMatched ||
                compareChar != currentQuerySubstring[currentQuerySubstringCharacterIndex])
            {
                matchFoundInPreviousLoop = false;
                continue;
            }

            if (firstMatchIndex < 0) firstMatchIndex = compareStringIndex;

            if (currentQuerySubstringCharacterIndex == 0)
            {
                matchFoundInPreviousLoop = true;
                firstMatchIndexInWord = compareStringIndex;
            }
            else if (!matchFoundInPreviousLoop)
            {
                // Verify there is no better match: the previous chars must be part of the pattern too.
                int startIndexToVerify = compareStringIndex - currentQuerySubstringCharacterIndex;
                if (AllPreviousCharsMatched(startIndexToVerify, currentQuerySubstringCharacterIndex,
                        compare, currentQuerySubstring))
                {
                    matchFoundInPreviousLoop = true;
                    if (currentQuerySubstringIndex == 0) firstMatchIndex = startIndexToVerify;
                    indexList = GetUpdatedIndexList(startIndexToVerify, currentQuerySubstringCharacterIndex,
                        firstMatchIndexInWord, indexList);
                }
            }

            lastMatchIndex = compareStringIndex + 1;
            indexList.Add(compareStringIndex);

            currentQuerySubstringCharacterIndex++;

            if (currentQuerySubstringCharacterIndex == currentQuerySubstring.Length)
            {
                allSubstringsContainedInCompareString =
                    matchFoundInPreviousLoop && allSubstringsContainedInCompareString;

                currentQuerySubstringIndex++;
                allQuerySubstringsMatched = currentQuerySubstringIndex >= querySubstrings.Length;
                if (allQuerySubstringsMatched) continue;

                currentQuerySubstring = querySubstrings[currentQuerySubstringIndex];
                currentQuerySubstringCharacterIndex = 0;
            }
        }

        if (acronymsMatched > 0 && acronymsMatched == query.Length)
        {
            int matchedGroups = CountDistinctAcronymGroups(acronymMatchData, compare);
            int acronymScore = acronymsTotalCount == 0 ? 0 : matchedGroups * 100 / acronymsTotalCount;
            if (acronymScore > 0) return new Result(true, acronymScore);
        }

        if (allQuerySubstringsMatched)
        {
            int nearestSpaceIndex = CalculateClosestSpaceIndex(spaceIndices, firstMatchIndex);
            int score = CalculateSearchScore(query, compare, firstMatchIndex - nearestSpaceIndex - 1,
                spaceIndices, lastMatchIndex - firstMatchIndex, allSubstringsContainedInCompareString);
            return new Result(true, score);
        }

        return new Result(false, 0);
    }

    private static bool IsAcronym(string text, int index)
    {
        if (IsAcronymChar(text, index) || IsAcronymNumber(text, index)) return true;
        return false;
    }

    private static bool IsAcronymCount(string text, int index)
    {
        if (IsAcronymChar(text, index)) return true;
        // Count only the first digit of a contiguous digit run as one group.
        if (IsAcronymNumber(text, index))
            return index == 0 || !IsAcronymNumber(text, index - 1);
        return false;
    }

    private static bool IsAcronymChar(string text, int index) =>
        char.IsUpper(text[index]) || index == 0 || char.IsWhiteSpace(text[index - 1]);

    private static bool IsAcronymNumber(string text, int index) => char.IsAsciiDigit(text[index]);

    private static int CountDistinctAcronymGroups(List<int> matchedIndices, string stringToCompare)
    {
        int groups = 0;
        var processed = new HashSet<int>();
        foreach (int matchedIndex in matchedIndices)
        {
            if (!processed.Add(matchedIndex)) continue;
            groups++;
            if (!IsAcronymNumber(stringToCompare, matchedIndex)) continue;
            int digitRunEnd = matchedIndex;
            while (digitRunEnd < stringToCompare.Length - 1 && IsAcronymNumber(stringToCompare, digitRunEnd + 1))
            {
                digitRunEnd++;
                processed.Add(digitRunEnd);
            }
        }
        return groups;
    }

    private static int CalculateClosestSpaceIndex(List<int> spaceIndices, int firstMatchIndex)
    {
        int closestSpaceIndex = -1;
        foreach (int index in spaceIndices)
        {
            if (index < firstMatchIndex) closestSpaceIndex = index;
            else break;
        }
        return closestSpaceIndex;
    }

    private static bool AllPreviousCharsMatched(int startIndexToVerify, int count,
        string stringToCompare, string currentQuerySubstring)
    {
        for (int i = 0; i < count; i++)
        {
            if (char.ToLowerInvariant(stringToCompare[startIndexToVerify + i]) != currentQuerySubstring[i])
                return false;
        }
        return true;
    }

    private static List<int> GetUpdatedIndexList(int startIndexToVerify, int count,
        int firstMatchIndexInWord, List<int> indexList)
    {
        var updated = new List<int>();
        indexList.RemoveAll(x => x >= firstMatchIndexInWord);
        updated.AddRange(indexList);
        for (int i = 0; i < count; i++) updated.Add(startIndexToVerify + i);
        return updated;
    }

    private static int CalculateSearchScore(string query, string stringToCompare, int firstIndex,
        List<int> spaceIndices, int matchLen, bool allSubstringsContainedInCompareString)
    {
        // Near the start and compact beats far and spread out.
        int score = 100 * (query.Length + 1) / ((1 + firstIndex) + (matchLen + 1));

        // "world" matching "world hello" should beat "hello world".
        if (firstIndex == 0 && allSubstringsContainedInCompareString)
            score -= spaceIndices.Count;

        if (stringToCompare.Length - query.Length < 5) score += 20;
        else if (stringToCompare.Length - query.Length < 10) score += 10;

        if (allSubstringsContainedInCompareString)
        {
            int count = 0;
            foreach (char c in query)
            {
                if (!char.IsWhiteSpace(c)) count++;
            }
            // 10 per char is too much for long queries: cap the linear part.
            int threshold = 4;
            score += count <= threshold ? count * 10 : threshold * 10 + (count - threshold) * 5;
        }

        return score;
    }
}
