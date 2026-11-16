using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SpotlightSearch.Controls;

/// <summary>
/// Small markdown renderer for AI answers: headings, lists, fenced code and inline
/// bold/italic/code. The original used Markdig + a native flow renderer; this keeps
/// the same visual result without the dependency.
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    public static FlowDocument Render(string markdown, FontFamily font, double fontSize = 13, bool boldBody = false)
    {
        var document = new FlowDocument
        {
            FontFamily = font,
            FontSize = fontSize,
            FontWeight = boldBody ? FontWeights.Bold : FontWeights.Normal,
            Foreground = BodyBrush,
            PagePadding = new Thickness(0),
            LineHeight = fontSize * 1.55
        };

        string[] lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        int index = 0;

        while (index < lines.Length)
        {
            string line = lines[index];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                string fence = line.TrimStart()[3..];
                index++;
                var code = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }
                index++; // closing fence

                document.Blocks.Add(CreateCodeBlock(string.Join("\n", code), fence, fontSize));
                continue;
            }

            if (line.Trim().Length == 0)
            {
                index++;
                continue;
            }

            if (IsHorizontalRule(line))
            {
                document.Blocks.Add(new Paragraph
                {
                    BorderBrush = CodeBackground,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 6, 0, 8)
                });
                index++;
                continue;
            }

            int headingLevel = HeadingLevel(line);
            if (headingLevel > 0)
            {
                string text = line.TrimStart('#').Trim();
                var heading = new Paragraph { Margin = new Thickness(0, headingLevel == 1 ? 10 : 8, 0, 4) };
                heading.Inlines.Add(new Run(text)
                {
                    FontSize = headingLevel switch { 1 => fontSize + 5, 2 => fontSize + 3, _ => fontSize + 1.5 },
                    FontWeight = FontWeights.Bold
                });
                document.Blocks.Add(heading);
                index++;
                continue;
            }

            if (TryGetListMarker(line, out string marker, out string listContent, out int indent))
            {
                var item = new Paragraph
                {
                    Margin = new Thickness(indent * 8, 0, 0, 2),
                    TextIndent = -10
                };
                item.Inlines.Add(new Run(marker + " ") { Foreground = MutedBrush });
                AppendInline(item, listContent);
                document.Blocks.Add(item);
                index++;
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
            var builder = new List<string> { line };
            index++;

            // Soft-wrap continuation lines into the same paragraph.
            while (index < lines.Length
                   && lines[index].Trim().Length > 0
                   && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && HeadingLevel(lines[index]) == 0
                   && !TryGetListMarker(lines[index], out _, out _, out _)
                   && !IsHorizontalRule(lines[index]))
            {
                builder.Add(lines[index]);
                index++;
            }

            AppendInline(paragraph, string.Join(" ", builder));
            document.Blocks.Add(paragraph);
        }

        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    private static Paragraph CreateCodeBlock(string code, string language, double fontSize)
    {
        var paragraph = new Paragraph
        {
            FontFamily = MonoFont,
            FontSize = fontSize - 1.5,
            Background = CodeBackground,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 8),
            LineHeight = (fontSize - 1.5) * 1.5
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            paragraph.Inlines.Add(new Run(language.Trim() + "\n")
            {
                FontSize = fontSize - 3,
                Foreground = MutedBrush
            });
        }

        paragraph.Inlines.Add(new Run(code));
        return paragraph;
    }

    private static void AppendInline(Paragraph paragraph, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (TryReadDelimited(text, i, "**", out string bold))
            {
                paragraph.Inlines.Add(new Bold(new Run(bold)));
                i += bold.Length + 4;
                continue;
            }

            if (TryReadDelimited(text, i, "`", out string code))
            {
                paragraph.Inlines.Add(new Run(code)
                {
                    FontFamily = MonoFont,
                    Background = CodeBackground
                });
                i += code.Length + 2;
                continue;
            }

            if (TryReadDelimited(text, i, "*", out string italic) && italic.Length > 1)
            {
                paragraph.Inlines.Add(new Italic(new Run(italic)));
                i += italic.Length + 2;
                continue;
            }

            int next = IndexOfNextMarker(text, i);
            paragraph.Inlines.Add(new Run(text[i..next]));
            i = next;
        }
    }

    private static bool TryReadDelimited(string text, int start, string delimiter, out string content)
    {
        content = string.Empty;
        if (start + delimiter.Length >= text.Length) return false;
        if (string.CompareOrdinal(text, start, delimiter, 0, delimiter.Length) != 0) return false;

        int end = text.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        if (end <= start + delimiter.Length) return false;

        content = text[(start + delimiter.Length)..end];
        return true;
    }

    private static int IndexOfNextMarker(string text, int start)
    {
        int next = text.Length;
        foreach (string marker in new[] { "**", "`", "*" })
        {
            int found = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (found >= 0 && found < next) next = found;
        }
        return next == start ? start + 1 : next;
    }

    private static int HeadingLevel(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#') level++;
        return level is >= 1 and <= 4 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static bool IsHorizontalRule(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(ch => ch is '-' or '*' or '_');
    }

    private static bool TryGetListMarker(string line, out string marker, out string content, out int indent)
    {
        marker = string.Empty;
        content = string.Empty;
        indent = 0;

        int spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ') spaces++;
        indent = spaces / 2;

        string trimmed = line[spaces..];
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            marker = "•";
            content = trimmed[2..];
            return true;
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            marker = trimmed[..(digits + 1)];
            content = trimmed[(digits + 2)..];
            return true;
        }

        return false;
    }
}
