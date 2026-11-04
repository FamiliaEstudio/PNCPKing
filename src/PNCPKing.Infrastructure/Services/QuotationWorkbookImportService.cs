using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationWorkbookImportService : IQuotationWorkbookImportService
{
    public const string ExternalAiSheetName = "PNCPKing_IA_v1";
    private static readonly string[] ExternalAiHeaders =
    [
        "Pesquisa", "Descrição de saída", "Quantidade", "Unidade", "Faixa mínima",
        "Faixa máxima", "Páginas por pesquisa", "Preços na cesta", "CATMAT",
        "Quantidade mínima por pedido", "Descritivo completo"
    ];
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
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadCore(sourcePath, cancellationToken), cancellationToken);

    private static QuotationImportDocument ReadCore(string sourcePath, CancellationToken cancellationToken)
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
            var isExternalAi = string.Equals(sheetName, ExternalAiSheetName, StringComparison.Ordinal);
            if (!isExternalAi && sheetName.StartsWith("PNCPKing_IA_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Versão do modelo de entrada para IA não suportada. Gere um modelo atualizado pelo programa.");
            var sharedStrings = ReadSharedStrings(workbookPart);
            var rows = ReadRows(worksheet.Part, sharedStrings, isExternalAi ? 11 : 10, cancellationToken);
            if (isExternalAi)
            {
                var header = rows.FirstOrDefault();
                if (header is null || header.Number != 1 ||
                    !ExternalAiHeaders.Select((value, index) => string.Equals(value,
                        header.Cells[index].Text, StringComparison.OrdinalIgnoreCase)).All(value => value))
                    throw new InvalidDataException($"{sheetName}!A1:K1 — mantenha os cabeçalhos do modelo de entrada para IA.");
                rows.RemoveAt(0);
            }
            if (LooksLikeResultWorkbook(sheetName, rows))
            {
                throw new InvalidDataException(
                    $"O arquivo \"{Path.GetFileName(sourcePath)}\" é uma planilha de resultado da cotação. " +
                    "Para importar, selecione a planilha de entrada com Pesquisa, Descrição, Quantidade, " +
                    "Unidade, Faixa mínima, Faixa máxima, Páginas locais e Número de preços na cesta nas colunas A:H; " +
                    "CATMAT em I e Quantidade mínima por pedido em J são opcionais.");
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
                        if (isExternalAi)
                            _ = SearchText.ParseMainCriteria(searchText);
                        else
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
                    var batchesDecimal = RequiredDecimal(row, 7, "Número de páginas locais", sheetName);
                    var basketSizeDecimal = OptionalDecimal(row, 8, "Número de preços na cesta", sheetName) ?? 3m;
                    var catmat = row.Cells[8].Text;
                    var minimumOrder = OptionalDecimal(row, 10, "Quantidade mínima por pedido", sheetName);
                    if (minimumOrder is { } nominal && (nominal <= 0 || !QuotationQuantity.IsValid(nominal)))
                        throw CellError(row, 10, sheetName, "Quantidade mínima por pedido",
                            "deve ser inteira e maior que zero; deixe vazia para usar o percentual na exportação.");
                    if (quantity <= 0 || !QuotationQuantity.IsValid(quantity))
                    {
                        throw CellError(
                            row,
                            3,
                            sheetName,
                            "Quantidade",
                            "deve ser um número inteiro maior que zero.");
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
                            "Número de páginas locais",
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
                        decimal.ToInt32(basketSizeDecimal),
                        PromptOrigin: isExternalAi ? SearchPromptOrigin.User : SearchPromptOrigin.Ai)
                    {
                        CatmatCodeOverride = catmat,
                        MinimumOrderQuantity = minimumOrder,
                        FullDescription = isExternalAi && row.Cells[10].Text.Length > 0
                            ? row.Cells[10].Text
                            : string.Empty
                    });
                }
                catch (CellValidationException exception)
                {
                    errors.Add(exception.Message);
                }
            }

            if (items.Count == 0 && errors.Count == 0)
            {
                errors.Add(isExternalAi
                    ? $"{sheetName}!A:K — preencha os itens a partir da linha 2."
                    : $"{sheetName}!A:J — a primeira planilha visível não contém itens nas colunas A:J.");
            }

            if (errors.Count > 0)
            {
                throw new InvalidDataException(
                    $"Arquivo \"{Path.GetFileName(sourcePath)}\", aba \"{sheetName}\":" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, errors));
            }

            return new QuotationImportDocument(Path.GetFullPath(sourcePath), items) { IsExternalAi = isExternalAi };
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

    public Task CreateExternalAiTemplateAsync(
        string destinationPath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(target), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Salve o modelo como arquivo .xlsx.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new Workbook();
                var styles = workbook.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = new Stylesheet(
                    new Fonts(new Font(), new Font(new Bold())),
                    new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }),
                        new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
                    new Borders(new Border()),
                    new CellStyleFormats(new CellFormat()),
                    new CellFormats(new CellFormat(),
                        new CellFormat { FontId = 1, ApplyFont = true },
                        new CellFormat { NumberFormatId = 49, ApplyNumberFormat = true }));
                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                var header = new Row { RowIndex = 1 };
                for (var index = 0; index < ExternalAiHeaders.Length; index++)
                    header.Append(TextCell(GetColumnName(index + 1) + "1", ExternalAiHeaders[index], 1));
                data.Append(header);
                var columns = new Columns();
                for (uint column = 1; column <= 11; column++)
                    columns.Append(new Column
                    {
                        Min = column, Max = column, CustomWidth = true,
                        Width = column is 1 or 2 or 11 ? 48 : column is 7 or 10 ? 28 : 18,
                        Style = column == 9 ? 2u : 0u
                    });
                part.Worksheet = new Worksheet(
                    new SheetViews(new SheetView(
                        new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft,
                            State = PaneStateValues.Frozen }) { WorkbookViewId = 0 }), columns, data);
                var instructions = workbook.AddNewPart<WorksheetPart>();
                var instructionData = new SheetData();
                var notes = new[]
                {
                    "Entrada para coleta e seleção por IA — formato 1",
                    "Preencha a aba PNCPKing_IA_v1 a partir da linha 2. Preserve o nome da aba e os cabeçalhos.",
                    "A: use + como na busca principal. Exemplo: copo descartável + copo plástico + copo polipropileno.",
                    "Cada trecho entre + é uma pesquisa independente; preencha todos os trechos e evite + consecutivos.",
                    "B: nome de saída. C: quantidade inteira positiva. D: unidade solicitada.",
                    "E/F: faixa opcional na unidade solicitada; a seleção final considera conversões justificadas.",
                    "G: páginas por expressão (1 a 100), com até 50 preços por página; limite atingido é coleta parcial.",
                    "H: quantidade de preços na cesta (3 a 10); vazio usa 3. I: CATMAT como texto.",
                    "J: quantidade mínima nominal por pedido, inteira positiva, opcional.",
                    "K: descritivo técnico completo, sem abreviar requisitos; vazio usa o texto de B.",
                    "A coleta cria os itens da cotação e gera dados para triagem e guia, sem baixar documentos.",
                    "Planilhas antigas sem identificação continuam usando as regras anteriores. Este modelo usa a sintaxe da busca principal."
                };
                for (var index = 0; index < notes.Length; index++)
                {
                    var row = new Row { RowIndex = (uint)index + 1 };
                    row.Append(TextCell("A" + (index + 1).ToString(CultureInfo.InvariantCulture), notes[index]));
                    instructionData.Append(row);
                }
                instructions.Worksheet = new Worksheet(
                    new Columns(new Column { Min = 1, Max = 1, Width = 130, CustomWidth = true }), instructionData);
                workbook.Workbook.Append(new Sheets(
                    new Sheet { Name = ExternalAiSheetName, SheetId = 1, Id = workbook.GetIdOfPart(part) },
                    new Sheet { Name = "Instruções", SheetId = 2, Id = workbook.GetIdOfPart(instructions) }));
                workbook.Workbook.Save();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }, cancellationToken);

    private static Cell TextCell(string reference, string text, uint style = 0) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        StyleIndex = style,
        InlineString = new InlineString(new Text(text))
    };

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

    private static List<SpreadsheetRow> ReadRows(
        WorksheetPart worksheetPart,
        IReadOnlyList<string> sharedStrings,
        int columnCount,
        CancellationToken cancellationToken)
    {
        var rows = new List<SpreadsheetRow>();
        using var reader = OpenXmlReader.Create(worksheetPart);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            var sourceRow = reader.LoadCurrentElement() as Row
                ?? throw new InvalidDataException("Não foi possível ler uma linha da planilha.");
            var cells = Enumerable
                .Repeat(new SpreadsheetCell(string.Empty, false, false), columnCount)
                .ToArray();
            foreach (var sourceCell in sourceRow.Elements<Cell>())
            {
                var column = GetColumnNumber(sourceCell.CellReference?.Value);
                if (column < 1 || column > columnCount)
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

    private static bool LooksLikeResultWorkbook(string sheetName, IReadOnlyList<SpreadsheetRow> rows)
    {
        var isLegacyResult =
            string.Equals(NormalizeCellText(sheetName), "Cotação", StringComparison.OrdinalIgnoreCase) &&
            rows.Any(row => string.Equals(
                row.Cells[2].Text,
                "INCISO II",
                StringComparison.OrdinalIgnoreCase));
        var isEvaluationTemplate =
            rows.Any(row => string.Equals(
                row.Cells[1].Text,
                "PLANILHA DE AVALIAÇÃO DE PREÇOS",
                StringComparison.OrdinalIgnoreCase)) &&
            rows.Any(row =>
                string.Equals(row.Cells[1].Text, "EMPRESA", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Cells[2].Text, "CNPJ", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Cells[4].Text, "VALOR DA COTAÇÃO", StringComparison.OrdinalIgnoreCase));

        return isLegacyResult || isEvaluationTemplate;
    }

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
