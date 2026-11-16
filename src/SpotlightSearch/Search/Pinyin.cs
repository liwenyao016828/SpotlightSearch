namespace SpotlightSearch.Search;

/// <summary>
/// Port of WinShun's src/core/Pinyin.cpp bit-parallel matcher: finding Chinese
/// names by pinyin. "bg", "baogao", "baog" and "bgao" all find 报告. Each Chinese
/// character matches a non-empty prefix of one of its readings (initials, whole
/// syllables, or a mix); every other character must match itself, ASCII folded.
///
/// The term is matched with a 64-bit state set over term positions, so one pass
/// over the name covers all readings and all split points at once.
/// </summary>
internal static class Pinyin
{
    private const uint ReadingMask = (1u << PinyinTable.ReadingBits) - 1;

    /// <summary>Whether a folded query term is worth trying as pinyin: 2..63 ASCII letters/digits, at least one letter.</summary>
    public static bool IsPinyinTerm(ReadOnlySpan<char> folded)
    {
        if (folded.Length < 2 || folded.Length > 63) return false;
        bool letter = false;
        foreach (char c in folded)
        {
            if (c is >= 'a' and <= 'z') letter = true;
            else if (c is < '0' or > '9') return false;
        }
        return letter;
    }

    public static bool HasHan(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (IsHan(c)) return true;
        }
        return false;
    }

    public static bool IsHan(char c) =>
        c is >= (char)PinyinTable.FirstHan and <= (char)PinyinTable.LastHan;

    public readonly record struct Match(int Start, int Length);

    /// <summary>Immutable matcher for one folded term; safe to reuse across candidates.</summary>
    internal sealed class Matcher
    {
        private readonly int _length; // 0: not a pinyin term
        private readonly bool _commonOnly;
        private readonly char _first;
        private readonly uint _firstInitial;
        private readonly ulong[] _at = new ulong[128];

        public Matcher(ReadOnlySpan<char> foldedTerm, bool commonOnly = false)
        {
            _commonOnly = commonOnly;
            if (!IsPinyinTerm(foldedTerm)) return;
            _length = foldedTerm.Length;
            _first = foldedTerm[0];
            _firstInitial = _first is >= 'a' and <= 'z' ? 1u << (_first - 'a') : 0;
            for (int p = 0; p < foldedTerm.Length; p++)
                _at[foldedTerm[p]] |= 1ul << p;
        }

        public bool IsValid => _length > 0;

        /// <summary>The leftmost match, or null.</summary>
        public Match? Find(string text)
        {
            if (_length == 0) return null;
            ulong done = 1ul << _length;
            for (int start = 0; start < text.Length; start++)
            {
                if (!CanStart(text[start])) continue;
                ulong states = 1; // nothing of the term matched yet
                for (int i = start; i < text.Length; i++)
                {
                    states = Step(text[i], states);
                    if ((states & done) != 0) return new Match(start, i + 1 - start);
                    if (states == 0) break;
                }
            }
            return null;
        }

        private bool CanStart(char c)
        {
            if (c < 0x80) return FoldAscii(c) == _first;
            return IsHan(c) && (Initials(c) & _firstInitial) != 0;
        }

        private ulong Step(char c, ulong states)
        {
            if (c < 0x80) return (states & _at[FoldAscii(c)]) << 1;

            ulong next = 0;
            uint packed = PinyinTable.Readings[c - PinyinTable.FirstHan];
            int readings = _commonOnly ? 1 : PinyinTable.MaxReadings;
            for (int k = 0; k < readings && packed != 0; k++, packed >>= PinyinTable.ReadingBits)
            {
                uint syllable = packed & ReadingMask;
                if (syllable == 0) continue;
                // Every non-empty prefix of the syllable: after k letters, t holds
                // the positions reached. 'v' (ü) also accepts 'u'.
                ulong t = states;
                foreach (char letter in PinyinTable.Syllables[syllable - 1])
                {
                    byte l = (byte)letter;
                    t = (t & (letter == 'v' ? _at[l] | _at['u'] : _at[l])) << 1;
                    if (t == 0) break;
                    next |= t;
                }
            }
            return next;
        }
    }

    /// <summary>The first letters of a character's readings, as bits (a = bit 0).</summary>
    public static uint Initials(char c)
    {
        if (!IsHan(c)) return 0;
        uint bits = 0;
        uint packed = PinyinTable.Readings[c - PinyinTable.FirstHan];
        for (int k = 0; k < PinyinTable.MaxReadings && packed != 0; k++, packed >>= PinyinTable.ReadingBits)
        {
            uint syllable = packed & ReadingMask;
            if (syllable != 0) bits |= 1u << (PinyinTable.Syllables[syllable - 1][0] - 'a');
        }
        return bits;
    }

    private static char FoldAscii(char c) =>
        c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
