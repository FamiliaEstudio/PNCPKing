using System.Diagnostics;
using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Services;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class QuotationWordTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PNCPKing.WordTests", Guid.NewGuid().ToString("N"));

    private static QuotationProject Project() => new(Guid.NewGuid(), "Tabela", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static QuotationLineAnalysis Item(QuotationProject project, string name = "Café Premium", decimal quantity = 100,
        decimal price = 1, Guid? group = null, decimal? minimum = null, string catmat = "") => new(new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = "descritor de pesquisa",
            DisplayName = name, RequestedQuantity = quantity, RequestedUnit = "pacote", GroupId = group,
            MinimumOrderQuantity = minimum, CatmatCodeOverride = catmat,
            SelectedBasketKey = "selected", SelectionConfirmed = true
        }, [], [new QuotationBasket
        {
            Key = "selected", References = [], AveragePrice = price, AdoptedPrice = price,
            MinimumPrice = price, MaximumPrice = price, MaximumDeviationPercent = 0, Score = 100
        }], 0, 0, 0, 0, 0);

    private async Task<string> Export(QuotationProjectReport report, QuotationWordExportOptions? options = null, bool includePrices = false)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Guid.NewGuid() + ".docx");
        if (includePrices) await new QuotationWordService().ExportPriceTableAsync(path, report);
        else await new QuotationWordService().ExportAsync(path, report, options ?? new());
        return path;
    }

    private static void Validate(WordprocessingDocument doc)
    {
        var errors = new OpenXmlValidator().Validate(doc).Take(20).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error =>
            $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}")));
    }

    [Theory]
    [InlineData(false, false, 5, false)]
    [InlineData(false, true, 6, false)]
    [InlineData(true, false, 6, false)]
    [InlineData(true, true, 7, false)]
    [InlineData(false, false, 6, true)]
    [InlineData(true, false, 7, true)]
    public async Task ColumnsMergesStylesAndTemplateInstructions(bool grouped, bool registration, int columns, bool includePrices)
    {
        var project = Project();
        Guid? group = grouped ? Guid.NewGuid() : null;
        var a = Item(project, "Água Mineral", group: group);
        var b = Item(project, "Café Premium", group: group);
        var c = Item(project, "Item avulso");
        QuotationLineAnalysis[] lines = [a, b, c];
        QuotationGroup[] groups = group is { } id ? [new(id, project.Id, "Lote", [a.Line.Id, b.Line.Id])] : [];
        if (grouped) project = project with { Organization = QuotationOrganization.Calculate(project, lines, groups) };
        var path = await Export(new(project, lines) { Groups = groups }, new()
            { IsPriceRegistration = registration, MinimumOrderPercentage = 10 }, includePrices);
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var body = doc.MainDocumentPart!.Document.Body!;
        var table = Assert.Single(body.Elements<Table>());
        var rows = table.Elements<TableRow>().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row => Assert.Equal(columns, row.Elements<TableCell>().Count()));
        Assert.Equal(includePrices ? 11115 : 11010, table.GetFirstChild<TableGrid>()!.Elements<GridColumn>().Sum(col => int.Parse(col.Width!)));
        Assert.NotNull(rows[0].TableRowProperties!.GetFirstChild<TableHeader>());
        Assert.All(rows[0].Elements<TableCell>(), cell => Assert.Equal("d9d9d9", cell.TableCellProperties!.Shading!.Fill!.Value));
        Assert.DoesNotContain("Inserir", body.InnerText);
        Assert.DoesNotContain("Quando houver", body.InnerText);
        Assert.DoesNotContain("Mesclar", body.InnerText);
        Assert.DoesNotContain("[Ou", body.InnerText);
        Assert.Contains("Água Mineral", body.InnerText);
        Assert.DoesNotContain("descritor de pesquisa", body.InnerText);
        var specification = rows[1].Elements<TableCell>().ElementAt(grouped ? 2 : 1);
        Assert.True(specification.GetFirstChild<Paragraph>()!.Elements<Run>().First().RunProperties!.Bold!.Val!.Value);
        if (includePrices)
        {
            Assert.Equal(a.Line.EffectiveDisplayName, specification.InnerText);
            Assert.Equal(["UNID", "QTDE", "Valor Unitário", "Valor Total"],
                rows[0].Elements<TableCell>().TakeLast(4).Select(cell => cell.InnerText));
            Assert.DoesNotContain("especificação detalhada", body.InnerText);
        }
        else
        {
            Assert.Equal("16", specification.Descendants<Run>().Last().RunProperties!.FontSize!.Val!.Value);
            Assert.DoesNotContain("9.1. O custo estimado", body.InnerText);
        }
        if (grouped)
        {
            Assert.Equal("Grupo 1", rows[1].Elements<TableCell>().First().InnerText);
            Assert.Equal(MergedCellValues.Restart, rows[1].Descendants<VerticalMerge>().Single().Val!.Value);
            Assert.Equal(MergedCellValues.Continue, rows[2].Descendants<VerticalMerge>().Single().Val!.Value);
            Assert.Empty(rows[3].Elements<TableCell>().First().InnerText);
            Assert.Empty(rows[3].Descendants<VerticalMerge>());
        }
        else Assert.Empty(table.Descendants<VerticalMerge>());
    }

    [Theory]
    [InlineData(QuotationDescriptionLocation.Item32, "", "no item 3.2 deste Termo de Referência.")]
    [InlineData(QuotationDescriptionLocation.Item5, "", "no item 5 deste Termo de Referência.")]
    [InlineData(QuotationDescriptionLocation.Memorial, "", "no Memorial Descritivo.")]
    [InlineData(QuotationDescriptionLocation.OtherItem, "7.1", "no item 7.1 deste Termo de Referência.")]
    public async Task DescriptionLocations(QuotationDescriptionLocation location, string number, string expected)
    {
        var project = Project();
        var path = await Export(new(project, [Item(project)]), new() { DescriptionLocation = location, OtherItemNumber = number });
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Contains("A especificação detalhada constará " + expected, doc.MainDocumentPart!.Document.InnerText);
    }

    [Theory]
    [InlineData(100, null, "10")]
    [InlineData(101, null, "11")]
    [InlineData(101, 7, "7")]
    public async Task NominalMinimumOverridesPercentageAndQuotasShareTheSameMinimum(int quantity, int? nominal, string expected)
    {
        var project = Project();
        var item = Item(project, quantity: quantity, price: 1000, minimum: nominal);
        project = project with { Organization = QuotationOrganization.Calculate(project, [item], []) };
        var path = await Export(new(project, [item]), new() { IsPriceRegistration = true, MinimumOrderPercentage = 10 });
        using var doc = WordprocessingDocument.Open(path, false);
        var rows = doc.MainDocumentPart!.Document.Descendants<TableRow>().Skip(1).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(expected, row.Elements<TableCell>().Last().InnerText));
        Assert.Equal(["1", "2"], rows.Select(row => row.Elements<TableCell>().First().InnerText));
        Assert.Equal([(quantity - quantity / 4).ToString(), (quantity / 4).ToString()],
            rows.Select(row => row.Elements<TableCell>().ElementAt(4).InnerText));
    }

    [Theory]
    [InlineData("001234", CatalogKind.Catmat, "001234")]
    [InlineData("", CatalogKind.Catmat, "98765")]
    [InlineData("", CatalogKind.Catser, "")]
    public async Task CatmatOverrideFallbackAndCatser(string code, CatalogKind kind, string expected)
    {
        var project = Project();
        var item = Item(project, catmat: code);
        item = item with { Line = item.Line with { CatalogSelection = new()
            { Kind = kind, Code = "98765", Description = "Catálogo" } } };
        var path = await Export(new(project, [item]));
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal(expected, doc.MainDocumentPart!.Document.Descendants<TableRow>().Skip(1).First()
            .Elements<TableCell>().ElementAt(2).InnerText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessiveMinimumDoesNotOverwriteDestination(bool nominal)
    {
        var project = Project();
        var item = Item(project, price: 1000, minimum: nominal ? 30 : null);
        project = project with { Organization = QuotationOrganization.Calculate(project, [item], []) };
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "existing.docx");
        await File.WriteAllTextAsync(path, "preservado");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new QuotationWordService().ExportAsync(path,
            new(project, [item]), new() { IsPriceRegistration = true, MinimumOrderPercentage = nominal ? 10 : 30 }));
        Assert.Contains("Item 2", error.Message);
        Assert.Contains("Café Premium", error.Message);
        Assert.Equal("preservado", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnorganizedGroupsStaleOrganizationAndEmptyQuantities(bool includePrices)
    {
        var project = Project();
        var groupId = Guid.NewGuid();
        var item = Item(project, group: groupId);
        QuotationGroup[] groups = [new(groupId, project.Id, "Grupo", [item.Line.Id])];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Export(new(project, [item]) { Groups = groups }, includePrices: includePrices));
        project = project with { Organization = QuotationOrganization.Calculate(project, [item], groups) };
        var changed = item with { Line = item.Line with { RequestedQuantity = 90 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Export(new(project, [changed]) { Groups = groups }, includePrices: includePrices));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Export(new(Project(), [Item(project, quantity: 0)]), includePrices: includePrices));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsInvalidPercentage(int percentage) => Assert.Throws<ArgumentException>(() =>
        new QuotationWordExportOptions { IsPriceRegistration = true, MinimumOrderPercentage = percentage }.Validate());

    [Theory]
    [InlineData(100, false)]
    [InlineData(1000, false)]
    [InlineData(100, true)]
    [InlineData(1000, true)]
    public async Task LargeExportUsesOnlyTheSuppliedReport(int count, bool includePrices)
    {
        var project = Project();
        var lines = Enumerable.Range(1, count).Select(index => Item(project,
            $"Item {index} — descrição longa com maiúsculas e minúsculas, acentos e especificação de apresentação")).ToArray();
        var timer = Stopwatch.StartNew();
        var path = await Export(new(project, lines), new() { IsPriceRegistration = true, MinimumOrderPercentage = 10 }, includePrices);
        timer.Stop();
        output.WriteLine($"Tabela {(includePrices ? "9.1" : "1.1")}, {count} itens: {timer.Elapsed.TotalMilliseconds:N0} ms; DOCX: {new FileInfo(path).Length:N0} bytes; sem repositório ou rede no exportador.");
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal(count + 1, doc.MainDocumentPart!.Document.Descendants<TableRow>().Count());
        Validate(doc);
        var reviewDirectory = Environment.GetEnvironmentVariable("PNCPKING_WORD_REVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(reviewDirectory))
        {
            Directory.CreateDirectory(reviewDirectory);
            File.Copy(path, Path.Combine(reviewDirectory, $"Tabela-{(includePrices ? "9.1" : "1.1")}-{count}.docx"), true);
        }
    }

    [Theory]
    [InlineData(false, "R$ 1,23", "R$ 123,00")]
    [InlineData(true, "R$ 1,2399", "R$ 123,9900")]
    public async Task PriceTableUsesSelectedAdoptedPriceAndTruncatesToProjectPrecision(bool medication, string unit, string total)
    {
        var project = Project() with { IsMedication = medication };
        var item = Item(project, price: 1.23999m);
        var selected = item.Baskets.Single() with { AveragePrice = 99, MedianPrice = 88,
            AggregationMethod = QuotationAggregationMethod.Median };
        item = item with { Baskets = [selected with { Key = "recommended", AdoptedPrice = 500, IsRecommended = true }, selected] };
        var path = await Export(new(project, [item]), includePrices: true);
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var cells = doc.MainDocumentPart!.Document.Descendants<TableRow>().ElementAt(1).Elements<TableCell>().ToArray();
        Assert.Equal(["1", "Café Premium", "pacote", "100", unit, total], cells.Select(cell => cell.InnerText));
        Assert.Equal(1.23999m, item.SelectedBasket!.AdoptedPrice);
    }

    [Theory]
    [InlineData(false, false, "R$ 1.000,12", "R$ 76.009,12", "R$ 25.003,00")]
    [InlineData(true, false, "R$ 1.000,12", "R$ 76.009,12", "R$ 25.003,00")]
    [InlineData(false, true, "R$ 1.000,1234", "R$ 76.009,3784", "R$ 25.003,0850")]
    [InlineData(true, true, "R$ 1.000,1234", "R$ 76.009,3784", "R$ 25.003,0850")]
    public async Task PriceTableUsesEachQuotaQuantity(bool grouped, bool medication, string unit, string principal, string reserved)
    {
        var project = Project() with { IsMedication = medication };
        Guid? groupId = grouped ? Guid.NewGuid() : null;
        var item = Item(project, quantity: 101, price: 1000.12349m, group: groupId);
        QuotationGroup[] groups = groupId is { } id ? [new(id, project.Id, "Grupo", [item.Line.Id])] : [];
        project = project with { Organization = QuotationOrganization.Calculate(project, [item], groups) };
        var path = await Export(new(project, [item]) { Groups = groups }, includePrices: true);
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var rows = doc.MainDocumentPart!.Document.Descendants<TableRow>().Skip(1)
            .Select(row => row.Elements<TableCell>().Select(cell => cell.InnerText).ToArray()).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(["1", "2"], rows.Select(row => row[grouped ? 1 : 0]));
        Assert.Equal(["76", "25"], rows.Select(row => row[^3]));
        Assert.All(rows, row => Assert.Equal(unit, row[^2]));
        Assert.Equal([principal, reserved], rows.Select(row => row[^1]));
        if (grouped) Assert.Equal(["Grupo 1", "Grupo 2"], rows.Select(row => row[0]));
    }

    [Fact]
    public async Task PriceTableWithoutOrganizationKeepsReportOrder()
    {
        var project = Project();
        var path = await Export(new(project, [Item(project, "Zinco"), Item(project, "Água")]), includePrices: true);
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal(["Zinco", "Água"], doc.MainDocumentPart!.Document.Descendants<TableRow>().Skip(1)
            .Select(row => row.Elements<TableCell>().ElementAt(1).InnerText));
    }

    [Theory]
    [InlineData("0.001", "zero reais")]
    [InlineData("0.01", "um centavo")]
    [InlineData("0.02", "dois centavos")]
    [InlineData("1", "um real")]
    [InlineData("1.01", "um real e um centavo")]
    [InlineData("19.99", "dezenove reais e noventa e nove centavos")]
    [InlineData("21.42", "vinte e um reais e quarenta e dois centavos")]
    [InlineData("100", "cem reais")]
    [InlineData("101.11", "cento e um reais e onze centavos")]
    [InlineData("999.98", "novecentos e noventa e nove reais e noventa e oito centavos")]
    [InlineData("1000", "mil reais")]
    [InlineData("1001", "mil e um reais")]
    [InlineData("1100", "mil e cem reais")]
    [InlineData("1101", "mil cento e um reais")]
    [InlineData("2000", "dois mil reais")]
    [InlineData("1000000", "um milhão de reais")]
    [InlineData("1000000.01", "um milhão de reais e um centavo")]
    [InlineData("1000001", "um milhão e um reais")]
    [InlineData("1200000", "um milhão e duzentos mil reais")]
    [InlineData("2000000", "dois milhões de reais")]
    [InlineData("1234567.89", "um milhão duzentos e trinta e quatro mil quinhentos e sessenta e sete reais e oitenta e nove centavos")]
    [InlineData("1000000000", "um bilhão de reais")]
    [InlineData("1000000000000", "um trilhão de reais")]
    public async Task PriceIntroductionHasExactTextWordsAndBoldBeforeTheTable(string price, string words)
    {
        var project = Project();
        var value = decimal.Parse(price, CultureInfo.InvariantCulture);
        var path = await Export(new(project, [Item(project, quantity: 1, price: value)]), includePrices: true);
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var table = Assert.Single(doc.MainDocumentPart!.Document.Body!.Elements<Table>());
        var introduction = Assert.IsType<Paragraph>(table.PreviousSibling());
        var amount = "R$ " + QuotationMoney.Truncate(value, 2).ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
        Assert.Equal($"9.1. O custo estimado total da contratação é de {amount} ({words}), conforme custos unitários apostos na tabela abaixo.",
            introduction.InnerText);
        var runs = introduction.Elements<Run>().ToArray();
        Assert.Equal([false, true, false], runs.Select(run => run.RunProperties!.Bold!.Val!.Value));
        Assert.Equal($"{amount} ({words}),", runs[1].InnerText);
        Assert.NotNull(introduction.ParagraphProperties!.KeepNext);
    }

    [Theory]
    [InlineData(false, "101.015,35", "cento e um mil e quinze reais e trinta e cinco centavos", "101015.35")]
    [InlineData(true, "101.015,71", "cento e um mil e quinze reais e setenta e um centavos", "101015.7143")]
    public async Task PriceIntroductionSumsQuotaRowsAndTruncatesOnlyTheFinalTotal(
        bool medication, string amount, string words, string exactTotal)
    {
        var project = Project() with { IsMedication = medication };
        var groupId = Guid.NewGuid();
        var large = Item(project, quantity: 101, price: 1000.12349m, group: groupId);
        var small = Item(project, "Zinco", quantity: 1, price: 1.23999m, group: groupId);
        var individual = Item(project, "Água", quantity: 2, price: 1.00559m);
        QuotationLineAnalysis[] lines = [large, small, individual];
        QuotationGroup[] groups = [new(groupId, project.Id, "Grupo", [large.Line.Id, small.Line.Id])];
        project = project with { Organization = QuotationOrganization.Calculate(project, lines, groups) };
        var path = await Export(new(project, lines) { Groups = groups }, includePrices: true);
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var table = Assert.Single(doc.MainDocumentPart!.Document.Body!.Elements<Table>());
        var introduction = Assert.IsType<Paragraph>(table.PreviousSibling());
        Assert.Equal($"R$ {amount} ({words}),", introduction.Elements<Run>().ElementAt(1).InnerText);
        var totals = table.Elements<TableRow>().Skip(1).Select(row => decimal.Parse(row.Elements<TableCell>().Last().InnerText,
            NumberStyles.Currency, CultureInfo.GetCultureInfo("pt-BR"))).ToArray();
        Assert.Equal(4, totals.Length);
        Assert.Equal(decimal.Parse(exactTotal, CultureInfo.InvariantCulture), totals.Sum());
        if (medication) Assert.Contains(2.0110m, totals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QuotaSuffixFollowsTheNameAndSmallItemsHaveNoSuffix(bool includePrices, bool grouped)
    {
        var project = Project();
        Guid? groupId = grouped ? Guid.NewGuid() : null;
        var item = Item(project, quantity: 101, price: 1000, group: groupId);
        var small = Item(project, "Zinco", quantity: 3, group: groupId);
        QuotationGroup[] groups = groupId is { } id ? [new(id, project.Id, "Grupo", [item.Line.Id, small.Line.Id])] : [];
        project = project with { Organization = QuotationOrganization.Calculate(project, [item, small], groups) };
        var path = await Export(new(project, [item, small]) { Groups = groups }, includePrices: includePrices);
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var names = doc.MainDocumentPart!.Document.Descendants<TableRow>().Skip(1)
            .Select(row => row.Elements<TableCell>().ElementAt(grouped ? 2 : 1)
                .Elements<Paragraph>().First().Elements<Run>().First().InnerText).ToArray();
        Assert.Equal(["Café Premium - Cota Principal", "Café Premium - Cota Reservada", "Zinco"], names);
        Assert.Equal("Café Premium", item.Line.EffectiveDisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThousandsUseBrazilianSeparatorsForQuantitiesAndPrices(bool includePrices)
    {
        var project = Project();
        var path = await Export(new(project, [Item(project, quantity: 12345, price: 1234.56m)]),
            new() { IsPriceRegistration = true, MinimumOrderPercentage = 10 }, includePrices);
        using var doc = WordprocessingDocument.Open(path, false);
        var cells = doc.MainDocumentPart!.Document.Descendants<TableRow>().ElementAt(1)
            .Elements<TableCell>().Select(cell => cell.InnerText).ToArray();
        Assert.Equal("12.345", cells[includePrices ? 3 : 4]);
        if (includePrices)
            Assert.Equal(["R$ 1.234,56", "R$ 15.240.643,20"], cells.TakeLast(2));
        else Assert.Equal("1.235", cells[^1]);
    }

    [Theory]
    [InlineData(false, "selected", 1)]
    [InlineData(true, null, 1)]
    [InlineData(true, "missing", 1)]
    [InlineData(true, "selected", 0)]
    [InlineData(true, "selected", -1)]
    public async Task PriceTableRequiresConfirmedSelectedBasketAndPreservesDestination(bool confirmed, string? key, int price)
    {
        var project = Project();
        var item = Item(project, price: price);
        item = item with { Line = item.Line with { SelectionConfirmed = confirmed, SelectedBasketKey = key } };
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "existing.docx");
        await File.WriteAllTextAsync(path, "preservado");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuotationWordService().ExportPriceTableAsync(path, new(project, [item])));
        Assert.Contains(item.Line.EffectiveDisplayName, error.Message);
        Assert.Contains("Confirme uma cesta", error.Message);
        Assert.Equal("preservado", await File.ReadAllTextAsync(path));
        // The specification table still supports items without confirmed prices.
        await Export(new(project, [item]));
    }

    [Fact]
    public async Task PriceTableCancellationPreservesDestination()
    {
        var project = Project();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "cancelled.docx");
        await File.WriteAllTextAsync(path, "preservado");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuotationWordService().ExportPriceTableAsync(path, new(project, [Item(project)]), cancellation.Token));
        Assert.Equal("preservado", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
