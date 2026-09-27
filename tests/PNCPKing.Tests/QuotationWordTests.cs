using System.Diagnostics;
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

    private async Task<string> Export(QuotationProjectReport report, QuotationWordExportOptions? options = null)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Guid.NewGuid() + ".docx");
        await new QuotationWordService().ExportAsync(path, report, options ?? new());
        return path;
    }

    private static void Validate(WordprocessingDocument doc)
    {
        var errors = new OpenXmlValidator().Validate(doc).Take(20).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error =>
            $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}")));
    }

    [Theory]
    [InlineData(false, false, 5)]
    [InlineData(false, true, 6)]
    [InlineData(true, false, 6)]
    [InlineData(true, true, 7)]
    public async Task ColumnsMergesStylesAndTemplateInstructions(bool grouped, bool registration, int columns)
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
            { IsPriceRegistration = registration, MinimumOrderPercentage = 10 });
        using var doc = WordprocessingDocument.Open(path, false);
        Validate(doc);
        var body = doc.MainDocumentPart!.Document.Body!;
        var table = Assert.Single(body.Elements<Table>());
        var rows = table.Elements<TableRow>().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row => Assert.Equal(columns, row.Elements<TableCell>().Count()));
        Assert.Equal(11010, table.GetFirstChild<TableGrid>()!.Elements<GridColumn>().Sum(col => int.Parse(col.Width!)));
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
        Assert.Equal("16", specification.Descendants<Run>().Last().RunProperties!.FontSize!.Val!.Value);
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

    [Fact]
    public async Task RejectsUnorganizedGroupsStaleOrganizationAndEmptyQuantities()
    {
        var project = Project();
        var groupId = Guid.NewGuid();
        var item = Item(project, group: groupId);
        QuotationGroup[] groups = [new(groupId, project.Id, "Grupo", [item.Line.Id])];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Export(new(project, [item]) { Groups = groups }));
        project = project with { Organization = QuotationOrganization.Calculate(project, [item], groups) };
        var changed = item with { Line = item.Line with { RequestedQuantity = 90 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Export(new(project, [changed]) { Groups = groups }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Export(new(Project(), [Item(project, quantity: 0)])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsInvalidPercentage(int percentage) => Assert.Throws<ArgumentException>(() =>
        new QuotationWordExportOptions { IsPriceRegistration = true, MinimumOrderPercentage = percentage }.Validate());

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task LargeExportUsesOnlyTheSuppliedReport(int count)
    {
        var project = Project();
        var lines = Enumerable.Range(1, count).Select(index => Item(project,
            $"Item {index} — descrição longa com maiúsculas e minúsculas, acentos e especificação de apresentação")).ToArray();
        var timer = Stopwatch.StartNew();
        var path = await Export(new(project, lines), new() { IsPriceRegistration = true, MinimumOrderPercentage = 10 });
        timer.Stop();
        output.WriteLine($"{count} itens: {timer.Elapsed.TotalMilliseconds:N0} ms; DOCX: {new FileInfo(path).Length:N0} bytes; sem repositório ou rede no exportador.");
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal(count + 1, doc.MainDocumentPart!.Document.Descendants<TableRow>().Count());
        Validate(doc);
        var reviewDirectory = Environment.GetEnvironmentVariable("PNCPKING_WORD_REVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(reviewDirectory))
        {
            Directory.CreateDirectory(reviewDirectory);
            File.Copy(path, Path.Combine(reviewDirectory, $"Tabela-1.1-{count}.docx"), true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
