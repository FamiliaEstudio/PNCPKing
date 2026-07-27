using System.Text;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class PdfToMarkdownConverter : IPdfToMarkdownConverter
{
    public Task<MarkdownConversionResult> ConvertAsync(
        DocumentTextIndex index,
        MarkdownConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        var markdown = new StringBuilder();
        var warnings = index.Warnings.ToList();
        foreach (var page in index.Pages.OrderBy(page => page.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.IncludePageHeadings)
            {
                if (markdown.Length > 0)
                {
                    markdown.AppendLine();
                }

                markdown.Append("## Página ").Append(page.PageNumber).AppendLine();
                markdown.AppendLine();
            }

            var lines = BuildLines(page);
            if (lines.Count == 0)
            {
                markdown.AppendLine("> Página sem texto pesquisável.");
                warnings.Add($"Página {page.PageNumber:N0}: nenhuma linha pôde ser convertida para Markdown.");
                continue;
            }

            WriteLines(markdown, lines, options.PreserveLineBreaks);
        }

        return Task.FromResult(new MarkdownConversionResult(markdown.ToString().TrimEnd(), warnings));
    }

    private static IReadOnlyList<MarkdownLine> BuildLines(DocumentPageIndex page)
    {
        if (page.Words.Count == 0)
        {
            return page.Blocks
                .OrderBy(block => block.Line)
                .Select(block => new MarkdownLine(block.Text.Trim(), [block.Text.Trim()]))
                .Where(line => line.Text.Length > 0)
                .ToArray();
        }

        var result = new List<MarkdownLine>();
        foreach (var group in page.Words.GroupBy(word => word.Line).OrderBy(group => group.Key))
        {
            var words = group.OrderBy(word => word.Bounds.X).ToArray();
            if (words.Length == 0)
            {
                continue;
            }

            var averageHeight = words.Average(word => Math.Max(1d, word.Bounds.Height));
            var cells = new List<StringBuilder> { new() };
            DocumentWord? previous = null;
            foreach (var word in words)
            {
                if (previous is not null)
                {
                    var gap = word.Bounds.X - (previous.Bounds.X + previous.Bounds.Width);
                    if (gap > Math.Max(18d, averageHeight * 2.4d))
                    {
                        cells.Add(new StringBuilder());
                    }
                    else if (cells[^1].Length > 0)
                    {
                        cells[^1].Append(' ');
                    }
                }

                cells[^1].Append(word.Text.Trim());
                previous = word;
            }

            var values = cells
                .Select(cell => cell.ToString().Trim())
                .Where(cell => cell.Length > 0)
                .ToArray();
            if (values.Length == 0)
            {
                continue;
            }

            result.Add(new MarkdownLine(string.Join(' ', values), values));
        }

        return result;
    }

    private static void WriteLines(StringBuilder output, IReadOnlyList<MarkdownLine> lines, bool preserveLineBreaks)
    {
        for (var index = 0; index < lines.Count;)
        {
            var tableColumnCount = lines[index].Cells.Count;
            var tableEnd = index;
            if (tableColumnCount >= 2)
            {
                while (tableEnd < lines.Count &&
                       lines[tableEnd].Cells.Count == tableColumnCount)
                {
                    tableEnd++;
                }
            }

            if (tableColumnCount >= 2 && tableEnd - index >= 2)
            {
                WriteTable(output, lines.Skip(index).Take(tableEnd - index).ToArray());
                index = tableEnd;
                continue;
            }

            var text = EscapeText(lines[index].Text);
            if (LooksLikeHeading(text))
            {
                output.Append("### ").AppendLine(text);
            }
            else
            {
                output.Append(text);
                output.AppendLine(preserveLineBreaks ? "  " : string.Empty);
            }

            index++;
        }
    }

    private static void WriteTable(StringBuilder output, IReadOnlyList<MarkdownLine> rows)
    {
        WriteTableRow(output, rows[0].Cells);
        output.Append('|');
        foreach (var _ in rows[0].Cells)
        {
            output.Append(" --- |");
        }

        output.AppendLine();
        foreach (var row in rows.Skip(1))
        {
            WriteTableRow(output, row.Cells);
        }

        output.AppendLine();
    }

    private static void WriteTableRow(StringBuilder output, IReadOnlyList<string> cells)
    {
        output.Append('|');
        foreach (var cell in cells)
        {
            output.Append(' ').Append(EscapeText(cell)).Append(" |");
        }

        output.AppendLine();
    }

    private static bool LooksLikeHeading(string text)
    {
        if (text.Length is < 3 or > 120)
        {
            return false;
        }

        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 3 &&
               letters.Count(char.IsUpper) / (double)letters.Length >= 0.80 &&
               !text.EndsWith('.');
    }

    private static string EscapeText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Trim();

    private sealed record MarkdownLine(string Text, IReadOnlyList<string> Cells);
}
