using ClosedXML.Excel;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationWorkbookService : IQuotationWorkbookService
{
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
            using var workbook = new XLWorkbook();
            WriteSimpleQuotation(workbook, report);
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

    private static void WriteSimpleQuotation(XLWorkbook workbook, QuotationProjectReport report)
    {
        var sheet = workbook.Worksheets.Add("Cotação");
        var row = 1;
        foreach (var analysis in report.Lines.OrderBy(line => line.Line.DisplayOrder))
        {
            var descriptionRange = sheet.Range(row, 1, row, 4);
            descriptionRange.Merge();
            descriptionRange.FirstCell().Value = analysis.Line.Description;
            descriptionRange.Style.Font.Bold = true;
            descriptionRange.Style.Font.FontSize = 12;
            descriptionRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
            descriptionRange.Style.Alignment.WrapText = true;
            row++;

            var selectedBasket = analysis.Line.SelectionConfirmed
                ? analysis.SelectedBasket
                : null;
            selectedBasket ??= analysis.Baskets.FirstOrDefault(basket => basket.IsRecommended);
            var references = (selectedBasket?.References
                ?? analysis.References
                    .Where(reference => reference.State == QuotationReferenceState.Eligible)
                    .OrderByDescending(reference => reference.Adequacy.Total)
                    .ThenBy(reference => reference.DistanceFromRibeiraoKilometers ?? double.MaxValue)
                    .ThenByDescending(reference => reference.ResultDate)
                    .ThenBy(reference => reference.Id, StringComparer.Ordinal)
                    .Take(analysis.Line.RequestedBasketSize)
                    .ToArray())
                .ToArray();

            foreach (var reference in references)
            {
                sheet.Cell(row, 1).Value = reference.SupplierName;
                sheet.Cell(row, 2).Value = FormatBrazilianTaxId(reference.SupplierTaxId);
                sheet.Cell(row, 2).Style.NumberFormat.Format = "@";
                sheet.Cell(row, 3).Value = "INCISO II";
                sheet.Cell(row, 4).Value = reference.UnitPrice;
                sheet.Cell(row, 4).Style.NumberFormat.Format = "R$ #,##0.0000";
                row++;
            }

            var minimumRows = selectedBasket?.IsManual == true
                ? 3
                : analysis.Line.RequestedBasketSize;
            for (var position = references.Length + 1; position <= minimumRows; position++)
            {
                sheet.Cell(row, 1).Value = $"Preço {position:N0} não obtido";
                sheet.Cell(row, 3).Value = "IMPOSSÍVEL OBTER PELO INCISO II";
                sheet.Range(row, 1, row, 4).Style.Font.FontColor = XLColor.DarkRed;
                sheet.Range(row, 1, row, 4).Style.Font.Italic = true;
                row++;
            }

            if (references.Length < minimumRows || selectedBasket?.VisualState == QuotationBasketVisualState.ManualInvalid)
            {
                var observation = sheet.Range(row, 1, row, 4);
                observation.Merge();
                observation.FirstCell().Value = selectedBasket is null
                    ? $"OBSERVAÇÃO: somente {references.Length:N0} de {minimumRows:N0} preço(s) válido(s) foram encontrados."
                    : $"OBSERVAÇÃO: {selectedBasket.ValidationMessage}";
                observation.Style.Font.Italic = true;
                observation.Style.Font.FontColor = XLColor.DarkRed;
                observation.Style.Alignment.WrapText = true;
                row++;
            }

            row++;
        }

        sheet.Column(1).Width = 42;
        sheet.Column(2).Width = 20;
        sheet.Column(3).Width = 15;
        sheet.Column(4).Width = 18;
        sheet.Column(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        sheet.SheetView.FreezeRows(1);
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
            sheet.Cell(row, 2).Value = analysis.Line.Description;
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
            "Atualidade", "Média da cesta", "Desvio máximo (%)", "Link PNCP", "Pesos do índice"
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
                sheet.Cell(row, 2).Value = analysis.Line.Description;
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
                sheet.Cell(row, 25).Value = "Abrir no PNCP";
                sheet.Cell(row, 25).SetHyperlink(new XLHyperlink(reference.PortalUrl));
                sheet.Cell(row, 26).Value = analysis.Line.Weights.ToString();
                row++;
            }

            itemNumber++;
        }

        sheet.Range(2, 7, Math.Max(2, row - 1), 7).Style.NumberFormat.Format = "R$ #,##0.0000";
        sheet.Range(2, 23, Math.Max(2, row - 1), 23).Style.NumberFormat.Format = "R$ #,##0.0000";
        sheet.Range(2, 12, Math.Max(2, row - 1), 12).Style.DateFormat.Format = "dd/mm/yyyy";
        sheet.Range(2, 16, Math.Max(2, row - 1), 24).Style.NumberFormat.Format = "0.00";
        FinishTable(sheet, 1, row - 1, headers.Length);
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
            sheet.Cell(row, 2).Value = analysis.Line.Description;
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
