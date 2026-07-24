using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationWorkbookImportService : IQuotationWorkbookImportService
{
    private static readonly NumberFormatInfo BrazilianNumberFormat = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        CurrencyDecimalSeparator = ",",
        CurrencyGroupSeparator = ".",
        CurrencySymbol = "R$"
    };

    public Task<QuotationImportDocument> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(sourcePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A importação aceita somente arquivos .xlsx.");
        }

        try
        {
            using var document = SpreadsheetDocument.Open(sourcePath, false);
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("O arquivo não possui uma pasta de trabalho do Excel.");
            var worksheet = FindFirstVisibleWorksheet(workbookPart)
                ?? throw new InvalidDataException("O arquivo não possui uma planilha visível.");
            var sheetName = worksheet.Sheet.Name?.Value ?? "Planilha";
            var sharedStrings = ReadSharedStrings(workbookPart);
            var rows = ReadRows(worksheet.Part, sharedStrings, cancellationToken);
            if (LooksLikeResultWorkbook(sheetName, rows))
            {
                throw new InvalidDataException(
                    $"O arquivo \"{Path.GetFileName(sourcePath)}\" é uma planilha de resultado da cotação. " +
                    "Para importar, selecione a planilha de entrada com Pesquisa, Descrição, Quantidade, " +
                    "Unidade, Faixa mínima, Faixa máxima, Disparos e Número de preços na cesta nas colunas A:H.");
            }

            var items = new List<QuotationImportItem>();
            var errors = new List<string>();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var searchText = RequiredText(row, 1, "Descrição da Pesquisa", sheetName);
                    try
                    {
                        _ = SearchText.Parse(searchText);
                    }
                    catch (SearchQueryException exception)
                    {
                        throw CellError(
                            row,
                            1,
                            sheetName,
                            "Descrição da Pesquisa",
                            $"expressão inválida: {exception.Message}");
                    }

                    var description = RequiredText(row, 2, "Descrição do Item", sheetName);
                    var quantity = RequiredDecimal(row, 3, "Quantidade", sheetName);
                    var unit = RequiredText(row, 4, "Unidade", sheetName);
                    var minimum = OptionalDecimal(row, 5, "Faixa mínima", sheetName);
                    var maximum = OptionalDecimal(row, 6, "Faixa máxima", sheetName);
                    var batchesDecimal = RequiredDecimal(row, 7, "Número de disparos", sheetName);
                    var basketSizeDecimal = OptionalDecimal(row, 8, "Número de preços na cesta", sheetName) ?? 3m;
                    if (quantity <= 0)
                    {
                        throw CellError(
                            row,
                            3,
                            sheetName,
                            "Quantidade",
                            "deve ser maior que zero.");
                    }

                    if (minimum < 0)
                    {
                        throw CellError(row, 5, sheetName, "Faixa mínima", "não pode ser negativa.");
                    }

                    if (maximum < 0)
                    {
                        throw CellError(row, 6, sheetName, "Faixa máxima", "não pode ser negativa.");
                    }

                    if (minimum is not null && maximum is not null && minimum > maximum)
                    {
                        throw CellError(
                            row,
                            5,
                            sheetName,
                            "Faixa mínima",
                            "não pode ser maior que a faixa máxima.");
                    }

                    if (batchesDecimal != decimal.Truncate(batchesDecimal) || batchesDecimal is < 1 or > 100)
                    {
                        throw CellError(
                            row,
                            7,
                            sheetName,
                            "Número de disparos",
                            "deve ser um inteiro de 1 a 100.");
                    }

                    if (basketSizeDecimal != decimal.Truncate(basketSizeDecimal) ||
                        basketSizeDecimal is < 3 or > 10)
                    {
                        throw CellError(
                            row,
                            8,
                            sheetName,
                            "Número de preços na cesta",
                            "deve ser um inteiro de 3 a 10; deixe vazio para usar 3.");
                    }

                    items.Add(new QuotationImportItem(
                        checked((int)row.Number),
                        searchText,
                        description,
                        quantity,
                        unit,
                        minimum,
                        maximum,
                        decimal.ToInt32(batchesDecimal),
                        decimal.ToInt32(basketSizeDecimal)));
                }
                catch (CellValidationException exception)
                {
                    errors.Add(exception.Message);
                }
            }

            if (items.Count == 0 && errors.Count == 0)
            {
                errors.Add($"{sheetName}!A:H — a primeira planilha visível não contém itens nas colunas A:H.");
            }

            if (errors.Count > 0)
            {
                throw new InvalidDataException(
                    $"Arquivo \"{Path.GetFileName(sourcePath)}\", aba \"{sheetName}\":" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, errors));
            }

            return Task.FromResult(new QuotationImportDocument(Path.GetFullPath(sourcePath), items));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or OpenXmlPackageException)
        {
            throw new InvalidDataException(
                $"Não foi possível ler o arquivo \"{Path.GetFileName(sourcePath)}\" como uma planilha XLSX válida: " +
                exception.Message,
                exception);
        }
    }

    private static (Sheet Sheet, WorksheetPart Part)? FindFirstVisibleWorksheet(WorkbookPart workbookPart)
    {
        foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.State is not null && sheet.State.Value != SheetStateValues.Visible ||
                sheet.Id?.Value is not { Length: > 0 } relationshipId ||
                workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            return (sheet, worksheetPart);
        }

        return null;
    }

    private static IReadOnlyList<string> ReadSharedStrings(WorkbookPart workbookPart) =>
        workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => NormalizeCellText(item.InnerText))
            .ToArray()
        ?? [];

    private static IReadOnlyList<SpreadsheetRow> ReadRows(
        WorksheetPart worksheetPart,
        IReadOnlyList<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var rows = new List<SpreadsheetRow>();
        foreach (var sourceRow in worksheetPart.Worksheet.Descendants<Row>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = Enumerable
                .Repeat(new SpreadsheetCell(string.Empty, false, false), 8)
                .ToArray();
            foreach (var sourceCell in sourceRow.Elements<Cell>())
            {
                var column = GetColumnNumber(sourceCell.CellReference?.Value);
                if (column is < 1 or > 8)
                {
                    continue;
                }

                cells[column - 1] = ReadCell(sourceCell, sharedStrings);
            }

            if (cells.All(cell => !cell.HasContent))
            {
                continue;
            }

            var rowNumber = sourceRow.RowIndex?.Value
                ?? sourceRow.Elements<Cell>()
                    .Select(cell => GetRowNumber(cell.CellReference?.Value))
                    .FirstOrDefault(number => number > 0);
            if (rowNumber == 0)
            {
                rowNumber = checked((uint)(rows.Count + 1));
            }

            rows.Add(new SpreadsheetRow(rowNumber, cells));
        }

        return rows;
    }

    private static SpreadsheetCell ReadCell(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        var dataType = cell.DataType?.Value;
        string value;
        var isNumeric = false;
        if (dataType == CellValues.SharedString)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index < 0 ||
                index >= sharedStrings.Count)
            {
                throw new InvalidDataException(
                    $"A célula {cell.CellReference?.Value ?? "(sem endereço)"} referencia um texto compartilhado inexistente.");
            }

            value = sharedStrings[index];
        }
        else if (dataType == CellValues.InlineString)
        {
            value = cell.InlineString?.InnerText ?? string.Empty;
        }
        else
        {
            value = raw;
            isNumeric = dataType is null || dataType == CellValues.Number;
        }

        return new SpreadsheetCell(
            NormalizeCellText(value),
            isNumeric,
            cell.CellFormula is not null);
    }

    private static string RequiredText(SpreadsheetRow row, int column, string label, string sheetName)
    {
        var value = row.Cells[column - 1].Text;
        return value.Length > 0
            ? value
            : throw CellError(row, column, sheetName, label, "é obrigatório.");
    }

    private static decimal RequiredDecimal(SpreadsheetRow row, int column, string label, string sheetName) =>
        OptionalDecimal(row, column, label, sheetName)
        ?? throw CellError(row, column, sheetName, label, "é obrigatório.");

    private static decimal? OptionalDecimal(
        SpreadsheetRow row,
        int column,
        string label,
        string sheetName)
    {
        var cell = row.Cells[column - 1];
        if (cell.Text.Length == 0)
        {
            return null;
        }

        if (cell.IsNumeric &&
            decimal.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        if (decimal.TryParse(
                cell.Text,
                NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                BrazilianNumberFormat,
                out numeric) ||
            decimal.TryParse(cell.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }

        throw CellError(
            row,
            column,
            sheetName,
            label,
            $"o valor \"{EscapeValue(cell.Text)}\" não é um número válido.");
    }

    private static bool LooksLikeResultWorkbook(string sheetName, IReadOnlyList<SpreadsheetRow> rows) =>
        string.Equals(NormalizeCellText(sheetName), "Cotação", StringComparison.OrdinalIgnoreCase) &&
        rows.Any(row => string.Equals(
            row.Cells[2].Text,
            "INCISO II",
            StringComparison.OrdinalIgnoreCase));

    private static CellValidationException CellError(
        SpreadsheetRow row,
        int column,
        string sheetName,
        string label,
        string message) =>
        new(
            $"Linha {row.Number}, {sheetName}!{GetColumnName(column)}{row.Number} — {label}: {message}");

    private static int GetColumnNumber(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return 0;
        }

        var column = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z')
            {
                break;
            }

            column = checked(column * 26 + character - 'A' + 1);
        }

        return column;
    }

    private static uint GetRowNumber(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return 0;
        }

        var digits = new string(reference.SkipWhile(char.IsAsciiLetter).ToArray());
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            ? row
            : 0;
    }

    private static string GetColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }

        return builder.ToString();
    }

    private static string NormalizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character) || character is '\u200B' or '\uFEFF')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string EscapeValue(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);

    private readonly record struct SpreadsheetCell(string Text, bool IsNumeric, bool HasFormula)
    {
        public bool HasContent => Text.Length > 0 || HasFormula;
    }

    private sealed record SpreadsheetRow(uint Number, SpreadsheetCell[] Cells);

    private sealed class CellValidationException(string message) : Exception(message);
}
