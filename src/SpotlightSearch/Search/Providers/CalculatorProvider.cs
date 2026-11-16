using System.Globalization;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search.Providers;

/// <summary>
/// Port of Native/Services/Spotlight/Providers/CalculatorProvider.cpp — a tiny
/// recursive-descent evaluator, not an expression library.
///
/// It only answers when the input actually looks like arithmetic (digit + operator),
/// so typing "7z" or "1password" never turns into a calculator result.
/// </summary>
internal sealed class CalculatorProvider : ISearchProvider
{
    internal const double ResultScore = 1100;

    public bool IsInstant => true;

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<SpotlightSearchItem> results =
            limit > 0 && TryEvaluate(query, out SpotlightSearchItem? item) ? [item!] : [];
        return Task.FromResult(results);
    }

    internal static bool TryEvaluate(string query, out SpotlightSearchItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(query)) return false;

        string expression = query.Trim();
        if (expression.Length > 64) return false;

        bool hasDigit = false;
        bool hasOperator = false;
        for (int i = 0; i < expression.Length; i++)
        {
            char ch = expression[i];
            if (char.IsAsciiDigit(ch))
            {
                hasDigit = true;
            }
            else if (char.IsWhiteSpace(ch) || ch is '(' or ')' or '.')
            {
                // neutral
            }
            else if (ch is '+' or '*' or '/' or '%')
            {
                hasOperator = true;
            }
            else if (ch == '-')
            {
                if (i >= 1) hasOperator = true;
            }
            else
            {
                return false;
            }
        }

        if (!hasDigit || !hasOperator) return false;

        int position = 0;
        if (!TryParseAddSubtract(expression, ref position, out double value, 0)) return false;
        SkipWhitespace(expression, ref position);
        if (position != expression.Length || !double.IsFinite(value)) return false;

        string display = value.ToString("0.##########", CultureInfo.CurrentCulture);
        item = new SpotlightSearchItem(
            $"calc:{expression}",
            SpotlightResultKind.Calculation,
            display,
            $"{expression} =",
            display)
        {
            Score = ResultScore
        };
        return true;
    }

    private static bool TryParseAddSubtract(string text, ref int position, out double value, int depth)
    {
        if (!TryParseMultiplyDivide(text, ref position, out value, depth) || !double.IsFinite(value))
            return false;

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] is not ('+' or '-')) return true;

            char op = text[position++];
            if (!TryParseMultiplyDivide(text, ref position, out double right, depth) || !double.IsFinite(right))
                return false;

            value = op == '+' ? value + right : value - right;
            if (!double.IsFinite(value)) return false;
        }
    }

    private static bool TryParseMultiplyDivide(string text, ref int position, out double value, int depth)
    {
        if (!TryParseFactor(text, ref position, out value, depth) || !double.IsFinite(value)) return false;

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] is not ('*' or '/' or '%')) return true;

            char op = text[position++];
            if (!TryParseFactor(text, ref position, out double right, depth) || !double.IsFinite(right))
                return false;

            if (op == '*')
            {
                value *= right;
            }
            else
            {
                if (right == 0) return false;
                value = op == '/' ? value / right : value % right;
            }

            if (!double.IsFinite(value)) return false;
        }
    }

    private static bool TryParseFactor(string text, ref int position, out double value, int depth)
    {
        value = 0;
        if (depth > 64) return false;

        SkipWhitespace(text, ref position);
        if (position >= text.Length) return false;

        if (text[position] is '+' or '-')
        {
            char sign = text[position++];
            if (!TryParseFactor(text, ref position, out double inner, depth + 1)) return false;
            value = sign == '-' ? -inner : inner;
            return double.IsFinite(value);
        }

        if (text[position] == '(')
        {
            position++;
            if (!TryParseAddSubtract(text, ref position, out value, depth + 1) || !double.IsFinite(value))
                return false;
            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] != ')') return false;
            position++;
            return true;
        }

        int start = position;
        while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.'))
            position++;

        return position > start && TryParseDecimal(text[start..position], out value) && double.IsFinite(value);
    }

    private static bool TryParseDecimal(string text, out double value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 64) return false;

        int dots = 0;
        int digits = 0;
        foreach (char ch in text)
        {
            if (char.IsAsciiDigit(ch)) digits++;
            else if (ch == '.' && ++dots <= 1) { }
            else return false;
        }

        if (digits == 0) return false;
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }
}
