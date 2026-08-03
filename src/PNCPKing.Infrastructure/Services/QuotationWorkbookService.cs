using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationWorkbookService : IQuotationWorkbookService
{
    private const string TemplateResourceName =
        "PNCPKing.Infrastructure.Assets.QuotationWorkbookTemplate.xlsx";
    private const int FirstBlockRow = 4;
    private const int TemplateLastRow = 24;
    private const int FirstPriceTemplateRow = 6;
    private const int MiddlePriceTemplateRow = 7;
    private const int LastPriceTemplateRow = 22;
    private const int SummaryTemplateRow = 23;
    private const int SpacerTemplateRow = 24;
    private const int MinimumPriceRows = 3;
    private const double MinimumHeaderRowHeight = 64.5d;

    private static readonly IReadOnlyDictionary<string, string> StatePhrases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = "Estado do Acre",
            ["AL"] = "Estado de Alagoas",
            ["AP"] = "Estado do Amapá",
            ["AM"] = "Estado do Amazonas",
            ["BA"] = "Estado da Bahia",
            ["CE"] = "Estado do Ceará",
            ["DF"] = "Distrito Federal",
            ["ES"] = "Estado do Espírito Santo",
            ["GO"] = "Estado de Goiás",
            ["MA"] = "Estado do Maranhão",
            ["MT"] = "Estado de Mato Grosso",
            ["MS"] = "Estado de Mato Grosso do Sul",
            ["MG"] = "Estado de Minas Gerais",
            ["PA"] = "Estado do Pará",
            ["PB"] = "Estado da Paraíba",
            ["PR"] = "Estado do Paraná",
            ["PE"] = "Estado de Pernambuco",
            ["PI"] = "Estado do Piauí",
            ["RJ"] = "Estado do Rio de Janeiro",
            ["RN"] = "Estado do Rio Grande do Norte",
            ["RS"] = "Estado do Rio Grande do Sul",
            ["RO"] = "Estado de Rondônia",
            ["RR"] = "Estado de Roraima",
            ["SC"] = "Estado de Santa Catarina",
            ["SP"] = "Estado de São Paulo",
            ["SE"] = "Estado de Sergipe",
            ["TO"] = "Estado do Tocantins"
        };

    public Task ExportAsync(
        string destinationPath,
        QuotationProjectReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destinationPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            destinationPath += ".xlsx";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partialPath = destinationPath + ".partial.xlsx";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            using var templateStream = OpenTemplateStream();
            using var workbook = new XLWorkbook(templateStream);
            WriteQuotationFromTemplate(workbook, report, cancellationToken);
            workbook.CalculateMode = XLCalculateMode.Auto;
            workbook.CalculationOnSave = true;
            workbook.ForceFullCalculation = true;
            workbook.FullCalculationOnLoad = true;
            cancellationToken.ThrowIfCancellationRequested();
            workbook.SaveAs(partialPath);
            File.Move(partialPath, destinationPath, true);
            return Task.CompletedTask;
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static MemoryStream OpenTemplateStream()
    {
        using var resource = typeof(QuotationWorkbookService).Assembly
            .GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidDataException(
                $"Modelo de exportação Excel ausente: {TemplateResourceName}.");
        var copy = new MemoryStream();
        resource.CopyTo(copy);
        copy.Position = 0;
        using (var document = SpreadsheetDocument.Open(copy, true))
        {
            var workbook = document.WorkbookPart?.Workbook
                ?? throw new InvalidDataException(
                    "O modelo de exportação Excel não possui uma pasta de trabalho.");
            workbook.DefinedNames?.Remove();
            workbook.Save();
        }

        copy.Position = 0;
        return copy;
    }

    private static void WriteQuotationFromTemplate(
        XLWorkbook workbook,
        QuotationProjectReport report,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.Worksheet(1);
        ResizeHeaderPicture(sheet);
        var prototype = workbook.Worksheets.Add("__PNCPKing_Block_Template");
        sheet.Range(FirstBlockRow, 1, TemplateLastRow, 10).CopyTo(prototype.Cell(1, 1));
        prototype.ConditionalFormats.RemoveAll();
        var templateRowHeights = Enumerable.Range(FirstBlockRow, TemplateLastRow - FirstBlockRow + 1)
            .ToDictionary(row => row, row => sheet.Row(row).Height);

        foreach (var mergedRange in sheet.MergedRanges
                     .Where(range => range.RangeAddress.FirstAddress.RowNumber >= FirstBlockRow)
                     .ToArray())
        {
            mergedRange.Unmerge();
        }

        sheet.ConditionalFormats.RemoveAll();
        sheet.Range(FirstBlockRow, 1, TemplateLastRow, 10).Clear(XLClearOptions.All);

        var analyses = report.Lines
            .OrderBy(line => line.Line.DisplayOrder)
            .ToArray();
        var row = FirstBlockRow;
        for (var itemIndex = 0; itemIndex < analyses.Length; itemIndex++)
        {
            var analysis = analyses[itemIndex];
            cancellationToken.ThrowIfCancellationRequested();
            CopyTemplateRow(
                prototype,
                sourceRow: 4,
                sheet,
                row,
                templateRowHeights,
                clearContents: true);
            var itemRange = sheet.Range(row, 2, row, 9);
            itemRange.Merge();
            itemRange.FirstCell().Value = $"Item {itemIndex + 1} - {FormatLineName(analysis.Line)}";
            row++;

            CopyTemplateRow(
                prototype,
                sourceRow: 5,
                sheet,
                row,
                templateRowHeights,
                clearContents: false);
            row++;

            var references = SelectExportedReferences(analysis);
            var priceRowCount = Math.Max(MinimumPriceRows, references.Count);
            var firstPriceRow = row;
            var lastPriceRow = checked(firstPriceRow + priceRowCount - 1);

            for (var index = 0; index < priceRowCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = index == 0
                    ? FirstPriceTemplateRow
                    : index == priceRowCount - 1
                        ? LastPriceTemplateRow
                        : MiddlePriceTemplateRow;
                CopyTemplateRow(
                    prototype,
                    sourceRow,
                    sheet,
                    row,
                    templateRowHeights,
                    clearContents: true);

                if (index < references.Count)
                {
                    WriteReference(sheet, row, references[index]);
                }
                else
                {
                    sheet.Cell(row, 2).Value = $"Preço {index + 1:N0} não obtido";
                    sheet.Range(row, 2, row, 9).Style.Font.FontColor = XLColor.DarkRed;
                    sheet.Range(row, 2, row, 9).Style.Font.Italic = true;
                }

                WritePriceFormulas(sheet, row, firstPriceRow, lastPriceRow);
                row++;
            }

            AddPriceConditionalFormatting(sheet, firstPriceRow, lastPriceRow);
            CopyTemplateRow(
                prototype,
                SummaryTemplateRow,
                sheet,
                row,
                templateRowHeights,
                clearContents: true);
            sheet.Cell(row, 2).Value = "Valor médio dos preços válidos";
            sheet.Range(row, 3, row, 5).Merge();
            sheet.Cell(row, 3).FormulaA1 =
                $"IFERROR(SUM(J{firstPriceRow}:J{lastPriceRow})/" +
                $"COUNTIF(J{firstPriceRow}:J{lastPriceRow},\">0\"),\"\")";
            row++;

            if (itemIndex < analyses.Length - 1)
            {
                CopyTemplateRow(
                    prototype,
                    SpacerTemplateRow,
                    sheet,
                    row,
                    templateRowHeights,
                    clearContents: true);
                row++;
            }
        }

        sheet.Column(10).Hide();
        workbook.Worksheets.Delete(prototype.Name);
    }

    private static void ResizeHeaderPicture(IXLWorksheet sheet)
    {
        var pictures = sheet.Pictures.ToArray();
        if (pictures.Length != 1)
        {
            throw new InvalidDataException(
                $"O modelo de exportação Excel deve conter exatamente uma imagem no cabeçalho; " +
                $"{pictures.Length:N0} encontrada(s).");
        }

        var picture = pictures[0];
        // Excel stores column widths in character units. The model uses the
        // standard 7-pixel maximum digit width, plus the 5-pixel cell padding.
        var maximumWidth = Math.Floor((sheet.Column(2).Width * 7d) + 5d);
        const double pixelsPerPoint = 96d / 72d;
        var maximumHeight = sheet.Row(2).Height * pixelsPerPoint;
        var scale = Math.Min(
            maximumWidth / picture.OriginalWidth,
            maximumHeight / picture.OriginalHeight);
        picture.Width = Math.Max(1, (int)Math.Floor(picture.OriginalWidth * scale));
        picture.Height = Math.Max(1, (int)Math.Floor(picture.OriginalHeight * scale));
        picture.MoveTo(sheet.Cell("B2"));
    }

    private static IReadOnlyList<QuotationReference> SelectExportedReferences(
        QuotationLineAnalysis analysis)
    {
        var selectedBasket = analysis.Line.SelectionConfirmed
            ? analysis.SelectedBasket
            : null;
        selectedBasket ??= analysis.Baskets.FirstOrDefault(basket => basket.IsRecommended);
        return (selectedBasket?.References
            ?? analysis.References
                .Where(reference =>
                    reference.State == QuotationReferenceState.Eligible &&
                    reference.Source == QuotationReferenceSource.PncpIncisoII)
                .OrderByDescending(reference => reference.Adequacy.Total)
                .ThenBy(reference => reference.DistanceFromRibeiraoKilometers ?? double.MaxValue)
                .ThenByDescending(reference => reference.ResultDate)
                .ThenBy(reference => reference.Id, StringComparer.Ordinal)
                .Take(analysis.Line.RequestedBasketSize)
                .ToArray())
            .ToArray();
    }

    private static void CopyTemplateRow(
        IXLWorksheet prototype,
        int sourceRow,
        IXLWorksheet destination,
        int destinationRow,
        IReadOnlyDictionary<int, double> templateRowHeights,
        bool clearContents)
    {
        prototype.Range(sourceRow - FirstBlockRow + 1, 1, sourceRow - FirstBlockRow + 1, 10)
            .CopyTo(destination.Cell(destinationRow, 1));
        destination.Row(destinationRow).Height = sourceRow == 5
            ? Math.Max(templateRowHeights[sourceRow], MinimumHeaderRowHeight)
            : templateRowHeights[sourceRow];
        if (sourceRow == 5)
        {
            var header = destination.Range(destinationRow, 2, destinationRow, 9);
            header.Style.Alignment.WrapText = true;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        if (clearContents)
        {
            destination.Range(destinationRow, 1, destinationRow, 10)
                .Clear(XLClearOptions.Contents);
        }
    }

    private static void WriteReference(
        IXLWorksheet sheet,
        int row,
        QuotationReference reference)
    {
        var supplierCell = sheet.Cell(row, 2);
        supplierCell.Value = FormatSupplierName(reference);
        supplierCell.Style.Alignment.WrapText = true;
        var minimumHeight = sheet.Row(row).Height;
        sheet.Row(row).AdjustToContents(2, 2);
        sheet.Row(row).Height = Math.Max(minimumHeight, sheet.Row(row).Height);
        sheet.Cell(row, 3).Value = FormatBrazilianTaxId(reference.SupplierTaxId);
        sheet.Cell(row, 3).Style.NumberFormat.Format = "@";
        if (Uri.TryCreate(reference.PortalUrl, UriKind.Absolute, out _))
        {
            sheet.Cell(row, 4).Value = reference.PortalUrl;
            sheet.Cell(row, 4).Style.NumberFormat.Format = "@";
        }

        sheet.Cell(row, 5).Value = reference.UnitPrice;
    }

    private static string FormatSupplierName(QuotationReference reference)
    {
        var supplierName = reference.SupplierName.Trim();
        if (supplierName.Length == 0)
        {
            return supplierName;
        }

        if (TryFormatLocation(reference.SupplierMunicipality, reference.SupplierUf, out var supplierLocation))
        {
            return $"{supplierName} ({supplierLocation})";
        }

        return TryFormatLocation(reference.Municipality, reference.Uf, out var buyerLocation)
            ? $"{supplierName} ({buyerLocation})"
            : supplierName;
    }

    private static bool TryFormatLocation(string? municipality, string? uf, out string location)
    {
        var normalizedMunicipality = municipality?.Trim() ?? string.Empty;
        var normalizedUf = uf?.Trim() ?? string.Empty;
        if (normalizedMunicipality.Length == 0 ||
            normalizedUf.Length == 0 ||
            !StatePhrases.ContainsKey(normalizedUf))
        {
            location = string.Empty;
            return false;
        }

        location = $"{normalizedMunicipality}/{normalizedUf.ToUpperInvariant()}";
        return true;
    }

    private static void WritePriceFormulas(
        IXLWorksheet sheet,
        int row,
        int firstPriceRow,
        int lastPriceRow)
    {
        var otherPrices = new List<string>(2);
        if (row > firstPriceRow)
        {
            otherPrices.Add($"E{firstPriceRow}:E{row - 1}");
        }

        if (row < lastPriceRow)
        {
            otherPrices.Add($"E{row + 1}:E{lastPriceRow}");
        }

        sheet.Cell(row, 6).FormulaA1 =
            $"IF(E{row}=\"\",\"\",IFERROR(AVERAGE({string.Join(",", otherPrices)}),\"\"))";
        sheet.Cell(row, 7).FormulaA1 =
            $"IF(OR(E{row}=\"\",F{row}=\"\"),\"\",E{row}/F{row}-1)";
        sheet.Cell(row, 8).FormulaA1 =
            $"IF(F{row}=\"\",\"\",IF(G{row}>0.25,\"EXCESSIVO\",\"VÁLIDO\"))";
        sheet.Cell(row, 9).FormulaA1 =
            $"IF(F{row}=\"\",\"\",IF(G{row}<-0.25,\"INEXEQUÍVEL\",\"VÁLIDO\"))";
        sheet.Cell(row, 10).FormulaA1 =
            $"IF(E{row}=\"\",\"\",IF(OR(H{row}=\"EXCESSIVO\",I{row}=\"INEXEQUÍVEL\"),\"\",E{row}))";
    }

    private static void AddPriceConditionalFormatting(
        IXLWorksheet sheet,
        int firstPriceRow,
        int lastPriceRow)
    {
        sheet.Range(firstPriceRow, 8, lastPriceRow, 8)
            .AddConditionalFormat()
            .WhenContains("EXCESSIVO")
            .Font.SetFontColor(XLColor.Red);
        sheet.Range(firstPriceRow, 8, lastPriceRow, 9)
            .AddConditionalFormat()
            .WhenContains("VÁLIDO")
            .Font.SetFontColor(XLColor.FromHtml("#548135"));
        sheet.Range(firstPriceRow, 9, lastPriceRow, 9)
            .AddConditionalFormat()
            .WhenContains("INEXEQUÍVEL")
            .Font.SetFontColor(XLColor.Red);
    }

    private static void WriteSummary(XLWorkbook workbook, QuotationProjectReport report)
    {
        var sheet = workbook.Worksheets.Add("Resumo");
        sheet.Cell(1, 1).Value = "Projeto";
        sheet.Cell(1, 2).Value = report.Project.Name;
        sheet.Cell(2, 1).Value = "Gerado em";
        sheet.Cell(2, 2).Value = DateTime.Now;
        sheet.Cell(2, 2).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        var headers = new[]
        {
            "Item", "Descrição solicitada", "Quantidade", "Unidade", "Situação", "Média",
            "Menor preço", "Maior preço", "Desvio máximo (%)", "Índice da cesta",
            "Amostra", "Elegíveis", "Duplicados", "Descartados", "Versão", "Pesos do índice"
        };
        WriteHeaders(sheet, 4, headers);
        var row = 5;
        var itemNumber = 1;
        foreach (var analysis in report.Lines)
        {
            var basket = analysis.Line.SelectionConfirmed ? analysis.SelectedBasket : null;
            sheet.Cell(row, 1).Value = itemNumber++;
            sheet.Cell(row, 2).Value = FormatLineName(analysis.Line);
            sheet.Cell(row, 3).Value = analysis.Line.RequestedQuantity;
            sheet.Cell(row, 4).Value = analysis.Line.RequestedUnit;
            sheet.Cell(row, 5).Value = basket is not null
                ? "Resolvido"
                : analysis.Baskets.Count == 0 ? "Sem cesta válida" : "Aguardando confirmação";
            if (basket is not null)
            {
                sheet.Cell(row, 6).Value = basket.AveragePrice;
                sheet.Cell(row, 7).Value = basket.MinimumPrice;
                sheet.Cell(row, 8).Value = basket.MaximumPrice;
                sheet.Cell(row, 9).Value = basket.MaximumDeviationPercent;
                sheet.Cell(row, 10).Value = basket.Score;
            }

            sheet.Cell(row, 11).Value = analysis.CollectedCount;
            sheet.Cell(row, 12).Value = analysis.EligibleCount;
            sheet.Cell(row, 13).Value = analysis.DuplicateCount;
            sheet.Cell(row, 14).Value = analysis.RejectedCount;
            sheet.Cell(row, 15).Value = analysis.Line.SampleVersion;
            sheet.Cell(row, 16).Value = analysis.Line.Weights.ToString();
            row++;
        }

        sheet.Range(5, 6, Math.Max(5, row - 1), 8).Style.NumberFormat.Format = "R$ #,##0.0000";
        sheet.Range(5, 9, Math.Max(5, row - 1), 10).Style.NumberFormat.Format = "0.00";
        FinishTable(sheet, 4, row - 1, headers.Length);
    }

    private static void WriteReferences(XLWorkbook workbook, QuotationProjectReport report)
    {
        var sheet = workbook.Worksheets.Add("Referências");
        var headers = new[]
        {
            "Item", "Descrição solicitada", "Qtd. solicitada", "Unidade solicitada", "Empresa", "CNPJ",
            "Preço unitário", "Descrição encontrada", "Unidade encontrada", "Qtd. homologada",
            "Qtd. do item", "Data", "Órgão", "Município", "UF", "Distância de Ribeirão (km)",
            "Adequação total", "Descrição", "Unidade/embalagem", "Quantidade", "Proximidade",
            "Atualidade", "Média da cesta", "Desvio máximo (%)", "Fonte", "Link da fonte",
            "Pesos do índice"
        };
        WriteHeaders(sheet, 1, headers);
        var row = 2;
        var itemNumber = 1;
        foreach (var analysis in report.Lines)
        {
            var basket = analysis.Line.SelectionConfirmed ? analysis.SelectedBasket : null;
            if (basket is null)
            {
                itemNumber++;
                continue;
            }

            foreach (var reference in basket.References)
            {
                sheet.Cell(row, 1).Value = itemNumber;
                sheet.Cell(row, 2).Value = FormatLineName(analysis.Line);
                sheet.Cell(row, 3).Value = analysis.Line.RequestedQuantity;
                sheet.Cell(row, 4).Value = analysis.Line.RequestedUnit;
                sheet.Cell(row, 5).Value = reference.SupplierName;
                sheet.Cell(row, 6).Value = FormatBrazilianTaxId(reference.SupplierTaxId);
                sheet.Cell(row, 6).Style.NumberFormat.Format = "@";
                sheet.Cell(row, 7).Value = reference.UnitPrice;
                sheet.Cell(row, 8).Value = reference.ItemDescription;
                sheet.Cell(row, 9).Value = reference.ItemUnit;
                if (reference.HomologatedQuantity is not null) sheet.Cell(row, 10).Value = reference.HomologatedQuantity.Value;
                if (reference.ItemRequestedQuantity is not null) sheet.Cell(row, 11).Value = reference.ItemRequestedQuantity.Value;
                if (reference.ResultDate is not null) sheet.Cell(row, 12).Value = reference.ResultDate.Value.ToDateTime(TimeOnly.MinValue);
                sheet.Cell(row, 13).Value = reference.Organization;
                sheet.Cell(row, 14).Value = reference.Municipality;
                sheet.Cell(row, 15).Value = reference.Uf;
                if (reference.DistanceFromRibeiraoKilometers is not null) sheet.Cell(row, 16).Value = reference.DistanceFromRibeiraoKilometers.Value;
                sheet.Cell(row, 17).Value = reference.Adequacy.Total;
                sheet.Cell(row, 18).Value = reference.Adequacy.DescriptionScore;
                sheet.Cell(row, 19).Value = reference.Adequacy.UnitScore;
                sheet.Cell(row, 20).Value = reference.Adequacy.QuantityScore;
                sheet.Cell(row, 21).Value = reference.Adequacy.ProximityScore;
                sheet.Cell(row, 22).Value = reference.Adequacy.RecencyScore;
                sheet.Cell(row, 23).Value = basket.AveragePrice;
                sheet.Cell(row, 24).Value = basket.MaximumDeviationPercent;
                sheet.Cell(row, 25).Value = FormatSource(reference.Source);
                if (Uri.TryCreate(reference.PortalUrl, UriKind.Absolute, out _))
                {
                    sheet.Cell(row, 26).Value = reference.PortalUrl;
                    sheet.Cell(row, 26).Style.NumberFormat.Format = "@";
                }
                sheet.Cell(row, 27).Value = analysis.Line.Weights.ToString();
                row++;
            }

            itemNumber++;
        }

        sheet.Range(2, 7, Math.Max(2, row - 1), 7).Style.NumberFormat.Format = "R$ #,##0.0000";
        sheet.Range(2, 23, Math.Max(2, row - 1), 23).Style.NumberFormat.Format = "R$ #,##0.0000";
        sheet.Range(2, 12, Math.Max(2, row - 1), 12).Style.DateFormat.Format = "dd/mm/yyyy";
        sheet.Range(2, 16, Math.Max(2, row - 1), 24).Style.NumberFormat.Format = "0.00";
        FinishTable(sheet, 1, row - 1, headers.Length);
        FormatPlainUrlColumn(sheet, 26, minimumWidth: 28);
    }

    private static void WritePending(XLWorkbook workbook, QuotationProjectReport report)
    {
        var sheet = workbook.Worksheets.Add("Pendências");
        var headers = new[]
        {
            "Item", "Descrição", "Situação", "Motivo", "Coletados", "Elegíveis", "Duplicados",
            "Descartados", "Cestas válidas", "Versão", "Amostra em"
        };
        WriteHeaders(sheet, 1, headers);
        var row = 2;
        var itemNumber = 1;
        foreach (var analysis in report.Lines)
        {
            if (analysis.Line.SelectionConfirmed && analysis.SelectedBasket is not null)
            {
                itemNumber++;
                continue;
            }

            sheet.Cell(row, 1).Value = itemNumber;
            sheet.Cell(row, 2).Value = FormatLineName(analysis.Line);
            sheet.Cell(row, 3).Value = analysis.Baskets.Count == 0 ? "Sem cesta válida" : "Aguardando confirmação";
            sheet.Cell(row, 4).Value = GetPendingReason(analysis);
            sheet.Cell(row, 5).Value = analysis.CollectedCount;
            sheet.Cell(row, 6).Value = analysis.EligibleCount;
            sheet.Cell(row, 7).Value = analysis.DuplicateCount;
            sheet.Cell(row, 8).Value = analysis.RejectedCount;
            sheet.Cell(row, 9).Value = analysis.Baskets.Count;
            sheet.Cell(row, 10).Value = analysis.Line.SampleVersion;
            sheet.Cell(row, 11).Value = analysis.Line.SampledAt.DateTime;
            row++;
            itemNumber++;
        }

        sheet.Range(2, 11, Math.Max(2, row - 1), 11).Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
        FinishTable(sheet, 1, row - 1, headers.Length);
    }

    private static string FormatLineName(QuotationLine line)
    {
        var suffix = line.CatalogSelection is null
            ? string.Empty
            : $" ({line.CatalogSelection.Label})";
        return line.EffectiveDisplayName + suffix;
    }

    private static string GetPendingReason(QuotationLineAnalysis analysis)
    {
        if (analysis.Baskets.Count > 0)
        {
            return analysis.Line.SelectedBasketKey is not null
                ? "A amostra foi atualizada e a escolha anterior precisa ser confirmada novamente."
                : "Há cestas válidas, mas nenhuma foi confirmada pelo usuário nesta versão.";
        }

        if (analysis.CollectedCount == 0)
        {
            return "Nenhum preço homologado ativo foi coletado para este item.";
        }

        if (analysis.EligibleCount < 2)
        {
            return $"Somente {analysis.EligibleCount:N0} referência(s) passou/passaram pela faixa de preço e pela compatibilidade descritiva.";
        }

        return $"Não foi possível formar a cesta automática de até {analysis.Line.RequestedBasketSize:N0} referências.";
    }

    private static void WriteMethodology(XLWorkbook workbook, QuotationProjectReport report)
    {
        var sheet = workbook.Worksheets.Add("Metodologia");
        var collected = report.Lines.Sum(line => line.CollectedCount);
        var eligible = report.Lines.Sum(line => line.EligibleCount);
        var duplicates = report.Lines.Sum(line => line.DuplicateCount);
        var rejected = report.Lines.Sum(line => line.RejectedCount);
        var rows = new (string Label, string Value)[]
        {
            ("Projeto", report.Project.Name),
            ("Versão das regras", QuotationAnalyzer.RulesVersion),
            ("Origem geográfica", "Ribeirão Preto/SP"),
            ("Fonte de preços", "Resultados unitários homologados ativos já coletados pela pesquisa do PNCP King"),
            ("Regra da cesta", "Cestas automáticas usam de 3 a 10 referências como alvo e podem ser reduzidas até 2. Cestas manuais preservam qualquer quantidade escolhida. CNPJ, origem, unidade, quantidade, desvio e índice permanecem auditáveis."),
            ("Índice da referência", "Os pesos de descrição, unidade/embalagem, quantidade, proximidade e atualidade são definidos por item e sempre somam 100%. O índice ordena e informa; não determina elegibilidade."),
            ("Adequação descritiva", "Mede a cobertura dos termos e expressões solicitados. Informações adicionais do item encontrado não reduzem a nota."),
            ("Quantidade", "Compara a escala das quantidades por faixas graduais; diferenças grandes reduzem a preferência, mas não anulam uma referência de preço unitário compatível."),
            ("Índice da cesta", "70% média das adequações + 20% menor adequação + 10% coesão de preços."),
            ("Auditoria dos pesos", "Os pesos efetivamente usados em cada item constam nas abas Resumo e Referências."),
            ("CNPJ e repetição", "Permanecem visíveis para auditoria e decisão do usuário; não excluem automaticamente uma referência compatível."),
            ("Conjunto de cestas", "Até 60 referências: 40 melhores por adequação, 10 menores preços e 10 maiores preços."),
            ("Volume da cotação", $"{collected:N0} coletados; {eligible:N0} elegíveis; {duplicates:N0} duplicados; {rejected:N0} descartados."),
            ("Observação", "O arquivo contém todas as cestas confirmadas e relaciona separadamente os itens pendentes.")
        };
        var row = 1;
        foreach (var (label, value) in rows)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        sheet.Range(1, 1, rows.Length, 1).Style.Font.Bold = true;
        sheet.Column(1).Width = 28;
        sheet.Column(2).Width = 110;
        sheet.Column(2).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteHeaders(IXLWorksheet sheet, int row, IReadOnlyList<string> headers)
    {
        for (var column = 1; column <= headers.Count; column++)
        {
            sheet.Cell(row, column).Value = headers[column - 1];
        }

        var range = sheet.Range(row, 1, row, headers.Count);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        range.Style.Font.FontColor = XLColor.White;
    }

    private static string FormatBrazilianTaxId(string value)
    {
        var normalized = new string(value
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalized.Length != 14 ||
            normalized[..12].Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            normalized[^2..].Any(character => !char.IsAsciiDigit(character)))
        {
            return value;
        }

        return $"{normalized[..2]}.{normalized[2..5]}.{normalized[5..8]}/" +
               $"{normalized[8..12]}-{normalized[12..]}";
    }

    private static string FormatSource(QuotationReferenceSource source) =>
        source == QuotationReferenceSource.InternetIncisoIII
            ? "INCISO III"
            : "INCISO II";

    private static void FormatPlainUrlColumn(
        IXLWorksheet sheet,
        int columnNumber,
        double minimumWidth)
    {
        const double maximumExcelWidth = 255;
        var column = sheet.Column(columnNumber);
        var longestText = column.CellsUsed()
            .Select(cell => cell.GetString().Length)
            .DefaultIfEmpty(0)
            .Max();
        var requestedWidth = Math.Max(minimumWidth, longestText + 2d);
        column.Width = Math.Min(maximumExcelWidth, requestedWidth);
        column.Style.Alignment.WrapText = requestedWidth > maximumExcelWidth;
    }

    private static void FinishTable(IXLWorksheet sheet, int headerRow, int lastRow, int lastColumn)
    {
        if (lastRow >= headerRow)
        {
            sheet.Range(headerRow, 1, Math.Max(headerRow, lastRow), lastColumn).SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(headerRow);
        sheet.ColumnsUsed().AdjustToContents();
        foreach (var column in sheet.ColumnsUsed())
        {
            column.Width = Math.Min(column.Width, 60d);
        }
    }
}
