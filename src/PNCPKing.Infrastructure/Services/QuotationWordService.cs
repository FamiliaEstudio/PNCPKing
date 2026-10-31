using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationWordService : IQuotationWordService
{
    private const string TemplateName = "PNCPKing.Infrastructure.Assets.QuotationWordTemplate.docx";
    private const string PriceTemplateName = "PNCPKing.Infrastructure.Assets.QuotationPriceWordTemplate.docx";
    private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("pt-BR");

    public Task ExportAsync(string destinationPath, QuotationProjectReport report,
        QuotationWordExportOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExportCoreAsync(destinationPath, report, options, false, cancellationToken), cancellationToken);

    public Task ExportPriceTableAsync(string destinationPath, QuotationProjectReport report,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExportCoreAsync(destinationPath, report, new(), true, cancellationToken), cancellationToken);

    private static async Task ExportCoreAsync(string destinationPath, QuotationProjectReport report,
        QuotationWordExportOptions options, bool includePrices, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        options.Validate();
        report.ValidateExport();
        if (report.Lines.Count == 0)
            throw new InvalidOperationException("A cotação não contém itens para exportar.");
        var organization = report.Project.Organization;
        if (organization is null && (report.Groups.Count > 0 || report.Lines.Any(item => item.Line.GroupId is not null)))
            throw new InvalidOperationException("Clique em Organizar Itens antes de exportar uma cotação com grupos.");

        var lines = report.Lines.ToDictionary(item => item.Line.Id, item => item.Line);
        var positions = organization?.Positions ?? report.Lines.Select((item, index) =>
            new QuotationPosition(index + 1, item.Line.Id, null, QuotationQuotaKind.None,
                item.Line.RequestedQuantity, 0, 0)).ToArray();
        var groups = organization?.Groups.ToDictionary(group => group.Id) ?? [];
        var minimums = new Dictionary<Guid, decimal>();
        var prices = new Dictionary<Guid, decimal>();
        foreach (var analysis in report.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = analysis.Line;
            if (line.RequestedQuantity <= 0 || !QuotationQuantity.IsValid(line.RequestedQuantity))
                throw new InvalidOperationException($"Informe quantidade inteira positiva para o item '{line.EffectiveDisplayName}'.");
            if (includePrices)
            {
                if (!line.SelectionConfirmed || analysis.SelectedBasket is not { AdoptedPrice: > 0 } basket)
                    throw new InvalidOperationException($"Confirme uma cesta com preço válido para o item '{line.EffectiveDisplayName}' antes de exportar a Tabela 9.1.");
                prices[line.Id] = QuotationMoney.Truncate(basket.AdoptedPrice, report.Project.PriceDecimalPlaces);
            }
            if (!options.IsPriceRegistration) continue;
            QuotationWordExportOptions.ValidateMinimumOrderQuantity(line.MinimumOrderQuantity);
            // Both quota positions share the original item's nominal or calculated minimum.
            minimums[line.Id] = line.MinimumOrderQuantity ??
                decimal.Ceiling(line.RequestedQuantity * (options.MinimumOrderPercentage!.Value / 100m));
        }
        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[position.LineId];
            if (position.Quantity <= 0 || !QuotationQuantity.IsValid(position.Quantity))
                throw new InvalidOperationException($"Informe quantidade inteira positiva para o item {position.Number} — {line.EffectiveDisplayName}.");
            if (options.IsPriceRegistration && minimums[position.LineId] > position.Quantity)
                throw new InvalidOperationException($"Item {position.Number} — {line.EffectiveDisplayName}: " +
                    $"a quantidade mínima ({minimums[position.LineId]:0}) supera a quantidade desta linha/cota ({position.Quantity:0}). " +
                    "Corrija ou limpe o mínimo nominal do item, ou reduza o percentual da exportação.");
        }

        using var buffer = new MemoryStream();
        using (var template = typeof(QuotationWordService).Assembly.GetManifestResourceStream(includePrices ? PriceTemplateName : TemplateName)
            ?? throw new InvalidOperationException("Modelo da tabela Word não encontrado."))
            await template.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        using (var document = WordprocessingDocument.Open(buffer, true))
        {
            var body = document.MainDocumentPart!.Document.Body!;
            NormalizeTemplate(document.MainDocumentPart.Document);
            if (document.MainDocumentPart.StyleDefinitionsPart?.Styles is { } styles)
            {
                foreach (var duplicate in styles.Elements<Style>().GroupBy(style => style.StyleId?.Value)
                             .SelectMany(group => group.Skip(1)).ToArray())
                    duplicate.Remove();
                NormalizeTemplate(styles);
            }
            var table = body.Elements<Table>().Single();
            var header = (TableRow)table.Elements<TableRow>().First().CloneNode(true);
            var prototype = (TableRow)table.Elements<TableRow>().Skip(1).First().CloneNode(true);
            var properties = (TableProperties)table.GetFirstChild<TableProperties>()!.CloneNode(true);
            var widths = table.GetFirstChild<TableGrid>()!.Elements<GridColumn>()
                .Select(column => int.Parse(column.Width!.Value!, CultureInfo.InvariantCulture)).ToArray();
            properties.TableWidth = new TableWidth
                { Width = widths.Sum().ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa };
            table.RemoveAllChildren();
            table.Append(properties);

            var hasGroups = positions.Any(position => position.ResultGroupId is not null);
            if (!hasGroups) widths[2] += widths[0];
            if (!includePrices && !options.IsPriceRegistration) widths[2] += widths[6];
            var columns = Enumerable.Range(0, 7)
                .Where(index => (index != 0 || hasGroups) && (index != 6 || includePrices || options.IsPriceRegistration)).ToArray();
            table.Append(new TableGrid(columns.Select(index => new GridColumn
                { Width = widths[index].ToString(CultureInfo.InvariantCulture) })));
            header.TableRowProperties ??= new TableRowProperties();
            header.TableRowProperties.RemoveAllChildren<TableHeader>();
            header.TableRowProperties.Append(new TableHeader());
            ConfigureColumns(header, columns, widths);
            table.Append(header);

            var estimatedTotal = 0m;
            Guid? previousGroup = null;
            foreach (var position in positions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = lines[position.LineId];
                var row = (TableRow)prototype.CloneNode(true);
                var cells = row.Elements<TableCell>().ToArray();
                foreach (var cell in cells)
                    cell.TableCellProperties?.RemoveAllChildren<VerticalMerge>();
                SetText(cells[0], string.Empty);
                if (position.ResultGroupId is { } groupId)
                {
                    var first = previousGroup != groupId;
                    cells[0].TableCellProperties!.VerticalMerge = new VerticalMerge
                        { Val = first ? MergedCellValues.Restart : MergedCellValues.Continue };
                    if (first) SetText(cells[0], $"Grupo {groups[groupId].Number}");
                }
                previousGroup = position.ResultGroupId;
                SetText(cells[1], position.Number.ToString(CultureInfo.InvariantCulture));
                var name = line.EffectiveDisplayName + (position.Kind switch
                {
                    QuotationQuotaKind.Principal => " - Cota Principal",
                    QuotationQuotaKind.Reserved => " - Cota Reservada",
                    _ => string.Empty
                });
                SetText(cells[2], name, includePrices ? null : options.DescriptionSentence, bold: true);
                if (includePrices)
                {
                    var unitPrice = prices[position.LineId];
                    var totalPrice = checked(unitPrice * position.Quantity);
                    estimatedTotal = checked(estimatedTotal + totalPrice);
                    SetText(cells[3], line.RequestedUnit);
                    SetText(cells[4], position.Quantity.ToString("N0", MoneyCulture));
                    SetText(cells[5], FormatMoney(unitPrice, report.Project.PriceDecimalPlaces));
                    SetText(cells[6], FormatMoney(totalPrice, report.Project.PriceDecimalPlaces));
                }
                else
                {
                    var catmat = !string.IsNullOrWhiteSpace(line.CatmatCodeOverride)
                        ? line.CatmatCodeOverride.Trim()
                        : line.CatalogSelection is { Kind: CatalogKind.Catmat } selection ? selection.Code : string.Empty;
                    SetText(cells[3], catmat);
                    SetText(cells[4], line.RequestedUnit);
                    SetText(cells[5], position.Quantity.ToString("N0", MoneyCulture));
                    SetText(cells[6], options.IsPriceRegistration
                        ? minimums[position.LineId].ToString("N0", MoneyCulture) : string.Empty);
                }
                ConfigureColumns(row, columns, widths);
                table.Append(row);
            }
            if (includePrices)
            {
                // Sum the exported quotas at their existing precision, then truncate only the introductory total.
                estimatedTotal = QuotationMoney.Truncate(estimatedTotal, 2);
                var introduction = table.PreviousSibling<Paragraph>()!;
                introduction.RemoveAllChildren<Run>();
                introduction.ParagraphProperties!.KeepNext = new KeepNext();
                introduction.Append(
                    TextRun("9.1. O custo estimado total da contratação é de ", false, "22"),
                    TextRun($"{FormatMoney(estimatedTotal, 2)} ({MoneyInWords(estimatedTotal)}),", true, "22"),
                    TextRun(" conforme custos unitários apostos na tabela abaixo.", false, "22"));
            }
            document.MainDocumentPart.Document.Save();
        }
        cancellationToken.ThrowIfCancellationRequested();
        buffer.Position = 0;
        // No destination is opened until the complete document has been assembled.
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void NormalizeTemplate(OpenXmlElement root)
    {
        // Normalize the supplied model's integer twips and on/off-only values once,
        // before cloning rows. Remove editor-only paragraph IDs (Word 2010).
        foreach (var element in root.Descendants().ToArray())
        {
            foreach (var attribute in element.GetAttributes().ToArray())
            {
                if (attribute.LocalName == "paraId")
                    element.RemoveAttribute(attribute.LocalName, attribute.NamespaceUri);
                else if (attribute.LocalName is "themeFillTint" or "themeFillShade" or "themeTint" or "themeShade" &&
                         attribute.Value is { Length: 6 } tint && tint.StartsWith("0000", StringComparison.Ordinal))
                    element.SetAttribute(new OpenXmlAttribute(attribute.Prefix, attribute.LocalName,
                        attribute.NamespaceUri, tint[4..]));
                else if (attribute.LocalName == "w" &&
                    decimal.TryParse(attribute.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) &&
                    value == decimal.Truncate(value))
                    element.SetAttribute(new OpenXmlAttribute(attribute.Prefix, attribute.LocalName,
                        attribute.NamespaceUri, value.ToString("0", CultureInfo.InvariantCulture)));
                else if (element is OnOffOnlyType && attribute.LocalName == "val")
                {
                    if (attribute.Value is "0" or "false" or "off") element.Remove();
                    else element.RemoveAttribute(attribute.LocalName, attribute.NamespaceUri);
                }
            }
        }
    }

    private static void ConfigureColumns(TableRow row, int[] columns, int[] widths)
    {
        var cells = row.Elements<TableCell>().ToArray();
        for (var index = 0; index < cells.Length; index++)
        {
            if (!columns.Contains(index)) { cells[index].Remove(); continue; }
            cells[index].TableCellProperties ??= new TableCellProperties();
            cells[index].TableCellProperties!.TableCellWidth = new TableCellWidth
                { Type = TableWidthUnitValues.Dxa, Width = widths[index].ToString(CultureInfo.InvariantCulture) };
        }
    }

    private static string FormatMoney(decimal value, int decimalPlaces) =>
        "R$ " + QuotationMoney.Truncate(value, decimalPlaces).ToString($"N{decimalPlaces}", MoneyCulture);

    private static string MoneyInWords(decimal value)
    {
        var reais = decimal.Truncate(value);
        var cents = (int)((value - reais) * 100m);
        var parts = new List<string>(2);
        if (reais > 0)
            parts.Add(NumberInWords(reais) + (reais == 1 ? " real" : reais % 1_000_000 == 0 ? " de reais" : " reais"));
        if (cents > 0)
            parts.Add(NumberInWords(cents) + (cents == 1 ? " centavo" : " centavos"));
        return parts.Count == 0 ? "zero reais" : string.Join(" e ", parts);
    }

    private static string NumberInWords(decimal value)
    {
        static string UnderThousand(int number)
        {
            if (number < 20) return number switch
            {
                0 => "zero", 1 => "um", 2 => "dois", 3 => "três", 4 => "quatro", 5 => "cinco",
                6 => "seis", 7 => "sete", 8 => "oito", 9 => "nove", 10 => "dez", 11 => "onze",
                12 => "doze", 13 => "treze", 14 => "quatorze", 15 => "quinze", 16 => "dezesseis",
                17 => "dezessete", 18 => "dezoito", _ => "dezenove"
            };
            if (number == 100) return "cem";
            var remainder = number < 100 ? number % 10 : number % 100;
            var prefix = number < 100 ? (number / 10) switch
            {
                2 => "vinte", 3 => "trinta", 4 => "quarenta", 5 => "cinquenta",
                6 => "sessenta", 7 => "setenta", 8 => "oitenta", _ => "noventa"
            } : (number / 100) switch
            {
                1 => "cento", 2 => "duzentos", 3 => "trezentos", 4 => "quatrocentos", 5 => "quinhentos",
                6 => "seiscentos", 7 => "setecentos", 8 => "oitocentos", _ => "novecentos"
            };
            return prefix + (remainder == 0 ? string.Empty : " e " + UnderThousand(remainder));
        }

        // Decimal totals can span up to ten groups of three digits.
        string[] scales = ["", "", "milh", "bilh", "trilh", "quadrilh", "quintilh", "sextilh", "setilh", "octilh"];
        var parts = new List<(int Number, string Text)>();
        for (var scale = 0; value > 0; scale++, value = decimal.Truncate(value / 1000m))
        {
            var number = (int)(value % 1000m);
            if (number == 0) continue;
            var text = UnderThousand(number);
            if (scale == 1) text = number == 1 ? "mil" : text + " mil";
            else if (scale > 1) text += " " + scales[scale] + (number == 1 ? "ão" : "ões");
            parts.Add((number, text));
        }
        parts.Reverse();
        return string.Concat(parts.Select((part, index) => (index == 0 ? string.Empty :
            index == parts.Count - 1 && (part.Number < 100 || part.Number % 100 == 0) ? " e " : " ") + part.Text));
    }

    private static void SetText(TableCell cell, string text, string? description = null, bool bold = false)
    {
        var paragraphProperties = cell.GetFirstChild<Paragraph>()?.ParagraphProperties?.CloneNode(true);
        cell.RemoveAllChildren<Paragraph>();
        var paragraph = new Paragraph();
        if (paragraphProperties is not null) paragraph.Append(paragraphProperties);
        paragraph.Append(TextRun(text, bold));
        if (description is not null)
        {
            paragraph.Append(new Run(new Break()));
            paragraph.Append(TextRun(description, false, "16"));
        }
        cell.Append(paragraph);
    }

    private static Run TextRun(string text, bool bold, string size = "18") => new(
        new RunProperties(new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
            new Bold { Val = bold }, new FontSize { Val = size }),
        new Text(text) { Space = SpaceProcessingModeValues.Preserve });
}
