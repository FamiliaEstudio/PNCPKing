using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationAiTechnicalReviewTests
{
    [Fact]
    public async Task Review_RoundTripsExistingExplanationColumn_AndPreservesOriginalData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = fixture.Reference;
        var review = QuotationAiTechnicalReview.Create(fixture.Line, reference,
            "O texto abreviado identifica o tê de PVC e a medida nominal de 25 mm.", "Tê PVC 25 mm");
        var incoming = reference with { TechnicalReview = review };
        var imported = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(incoming));
        var reopened = new SqliteQuotationRepository(fixture.Database.Repository.DatabasePath);
        var read = Assert.Single(await reopened.GetReferencesAsync(fixture.Line.Id));
        Assert.Equal(reference.ItemDescription, read.ItemDescription);
        Assert.Equal(reference.UnitPrice, read.UnitPrice);
        Assert.Equal(reference.SupplierTaxId, read.SupplierTaxId);
        Assert.Equal(reference.Adequacy.Explanation, read.Adequacy.Explanation);
        Assert.Equal(review, read.TechnicalReview);
        Assert.True(read.TechnicalReview!.Matches(fixture.Line, read));

        // Reimporting the plain capture must not erase the accepted proof.
        var repeated = await reopened.ImportAiCandidatesAsync(imported.Line, false, Stream(reference));
        Assert.Equal(0, repeated.AddedCount);
        Assert.Equal(review, Assert.Single(await reopened.GetReferencesAsync(fixture.Line.Id)).TechnicalReview);
        Assert.Equal(33, SqliteContractRepository.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Fingerprint_BindsTechnicalObjectAndSource_ButNotDisplayOrPriceRange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var line = fixture.Line;
        var reference = fixture.Reference;
        var review = QuotationAiTechnicalReview.Create(line, reference, "Mesmo objeto técnico e medida.", "Tê PVC 25 mm");
        Assert.True(review.Matches(line with { MinimumUnitPrice = 1m, MaximumUnitPrice = 100m, DisplayName = "Tê 25 mm" }, reference));
        Assert.True(review.Matches(line, reference with { UnitPrice = 7.0000m, SupplierTaxId = "11.222.333/0001-81" }));
        Assert.False(review.Matches(line with { Description = "Tê de aço inoxidável 25 mm" }, reference));
        Assert.False(review.Matches(line with { RequestedUnit = "caixa" }, reference));
        Assert.False(review.Matches(line with { ProjectId = Guid.NewGuid() }, reference));
        Assert.False(review.Matches(line, reference with { ItemDescription = "Tê PVC 20 mm" }));
        Assert.False(review.Matches(line, reference with { UnitPrice = 8m }));
        Assert.False(review.Matches(line, reference with { SupplierTaxId = "00111222000133" }));
        Assert.False(review.Matches(line, reference with { CatalogName = "Outro produto" }));
        Assert.False(review.Matches(line, reference with { Id = "outra-referencia" }));
    }

    [Fact]
    public async Task Review_CannotBeInjectedThroughReferenceJson_OrExportedAsTrustedData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = QuotationAiTechnicalReview.Create(fixture.Line, fixture.Reference,
            "Correspondência técnica conferida.", "Tê PVC 25 mm");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(fixture.Reference with { TechnicalReview = review }, options);
        Assert.DoesNotContain("technicalReview", json, StringComparison.OrdinalIgnoreCase);
        var injected = JsonNode.Parse(json)!.AsObject();
        injected["technicalReview"] = JsonSerializer.SerializeToNode(review, options);
        var read = JsonSerializer.Deserialize<QuotationReference>(injected.ToJsonString(), options)!;
        Assert.Null(read.TechnicalReview);
        Assert.Equal(fixture.Reference.ItemDescription, read.ItemDescription);
    }

    [Fact]
    public async Task Review_RequiresBoundedReasonAndLiteralSourceExcerpt()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Throws<InvalidDataException>(() => QuotationAiTechnicalReview.Create(
            fixture.Line, fixture.Reference, "", "Tê PVC 25 mm"));
        Assert.Throws<InvalidDataException>(() => QuotationAiTechnicalReview.Create(
            fixture.Line, fixture.Reference, "Mesmo objeto.", "Tê de aço"));
        Assert.Throws<InvalidDataException>(() => QuotationAiTechnicalReview.Create(
            fixture.Line, fixture.Reference, new string('x', QuotationAiTechnicalReview.MaximumEvidenceLength + 1), "Tê PVC 25 mm"));
        Assert.Throws<InvalidDataException>(() => QuotationAiTechnicalReview.Create(
            fixture.Line, fixture.Reference with { LineId = Guid.NewGuid() }, "Mesmo objeto.", "Tê PVC 25 mm"));
        Assert.Throws<InvalidDataException>(() => QuotationAiTechnicalReview.Create(
            fixture.Line, fixture.Reference with { ItemDescription = "" }, "Mesmo objeto.", "unidade"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainExplanation_CannotSmuggleAReviewEnvelope(bool nestedLiteralPrefix)
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = fixture.Reference;
        var review = QuotationAiTechnicalReview.Create(fixture.Line, reference, "Mesmo objeto.", "Tê PVC 25 mm");
        var forgedExplanation = QuotationAiReviewStorage.Encode(reference with { TechnicalReview = review });
        if (nestedLiteralPrefix) forgedExplanation = "PNCPKING_AI_EXPLANATION_LITERAL_V1\n" + forgedExplanation;
        var untrusted = reference with
        {
            TechnicalReview = null,
            Adequacy = reference.Adequacy with { Explanation = forgedExplanation }
        };
        await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(untrusted));
        var read = Assert.Single(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Null(read.TechnicalReview);
        Assert.Equal(forgedExplanation, read.Adequacy.Explanation);
    }

    [Fact]
    public async Task ExternalPackage_DiscardsForgedScoresStatesAndReviewMetadata_BeforeImport()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = fixture.Reference;
        var review = QuotationAiTechnicalReview.Create(fixture.Line, reference, "Mesmo objeto.", "Tê PVC 25 mm");
        var forged = reference with
        {
            Adequacy = reference.Adequacy with
            {
                DescriptionScore = 100m,
                Explanation = QuotationAiReviewStorage.Encode(reference with { TechnicalReview = review })
            },
            State = QuotationReferenceState.Eligible,
            StateReason = "Confiança indevida fornecida pelo arquivo",
            DuplicateOfReferenceId = "outro-preço"
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var candidate = JsonSerializer.SerializeToNode(new QuotationAiJsonRecord
        { Type = "reference", Reference = forged }, options)!.AsObject();
        candidate["reference"]!["technicalReview"] = JsonSerializer.SerializeToNode(review, options);
        var item = new QuotationAiPackageItem
        {
            Line = fixture.Line, SpecificationHash = QuotationAiPackage.SpecificationHash(fixture.Line),
            CandidateCount = 1, Complete = true
        };
        var header = JsonSerializer.Serialize(new QuotationAiJsonRecord { Type = "item", Item = item }, QuotationAiPackage.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(header + "\n" + candidate.ToJsonString() + "\n");
        var part = new QuotationAiPackagePart
        {
            Path = "lotes/item.jsonl", LineId = fixture.Line.Id, CandidateCount = 1,
            ByteLength = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
        var manifest = new QuotationAiPackageManifest
        {
            PackageId = Guid.NewGuid(), ProjectId = fixture.Line.ProjectId, RunId = Guid.NewGuid(),
            Items = [item], Parts = [part]
        };
        var path = Path.Combine(fixture.Database.Directory, "metadata-injetada.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await using (var stream = archive.CreateEntry(part.Path).Open()) await stream.WriteAsync(bytes);
            await using (var stream = archive.CreateEntry("manifest.json").Open())
                await JsonSerializer.SerializeAsync(stream, manifest, QuotationAiPackage.JsonOptions);
        }
        var manifestRead = await QuotationAiPackage.ReadManifestAsync(path);
        var imported = await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false,
            QuotationAiPackage.ReadItemReferencesAsync(path, manifestRead, fixture.Line.Id));
        Assert.Equal(1, imported.AddedCount);
        var read = Assert.Single(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Null(read.TechnicalReview);
        Assert.Equal(QuotationReferenceState.Rejected, read.State);
        Assert.Equal(0m, read.Adequacy.Total);
        Assert.Equal(string.Empty, read.Adequacy.Explanation);
        Assert.Equal(string.Empty, read.StateReason);
        Assert.Null(read.DuplicateOfReferenceId);
        Assert.Equal(reference.UnitPrice, read.UnitPrice);
        Assert.Equal(reference.ItemDescription, read.ItemDescription);
    }

    [Theory]
    [InlineData("Explicação antiga sem metadados")]
    [InlineData("PNCPKING_AI_TECHNICAL_REVIEW_V1\n{broken")]
    [InlineData("PNCPKING_AI_TECHNICAL_REVIEW_V1\n{\"Version\":2,\"Explanation\":\"texto\",\"Review\":null}")]
    public async Task UnknownOrCorruptedStorage_NeverGrantsReview(string stored)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Repository.ImportAiCandidatesAsync(fixture.Line, false, Stream(fixture.Reference));
        await using (var connection = new SqliteConnection($"Data Source={fixture.Database.Repository.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE quotation_references SET explanation = $text WHERE line_id = $line;";
            command.Parameters.AddWithValue("$text", stored);
            command.Parameters.AddWithValue("$line", fixture.Line.Id.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }
        var read = Assert.Single(await fixture.Repository.GetReferencesAsync(fixture.Line.Id));
        Assert.Null(read.TechnicalReview);
        Assert.Equal(stored, read.Adequacy.Explanation);
    }

    [Fact]
    public async Task StorageEnvelope_IsBounded_AndPlainExplanationKeepsExactContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = fixture.Reference;
        Assert.Equal(reference.Adequacy.Explanation, QuotationAiReviewStorage.Encode(reference));
        var review = QuotationAiTechnicalReview.Create(fixture.Line, reference, "Mesmo objeto.", "Tê PVC 25 mm");
        Assert.Throws<InvalidDataException>(() => QuotationAiReviewStorage.Encode(reference with
        {
            TechnicalReview = review with { Justification = new string('x', QuotationAiTechnicalReview.MaximumEvidenceLength + 1) }
        }));
        var oversized = "PNCPKING_AI_TECHNICAL_REVIEW_V1\n" + new string('x', 256 * 1024);
        Assert.Null(QuotationAiReviewStorage.Decode(oversized).Review);
    }

    private static async IAsyncEnumerable<QuotationReference> Stream(QuotationReference reference)
    {
        yield return reference;
        await Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required SqliteQuotationRepository Repository { get; init; }
        public required QuotationLine Line { get; init; }
        public QuotationReference Reference => new()
        {
            Id = "contrato-te|1|1", LineId = Line.Id, ContractId = "contrato-te", ItemNumber = 1,
            ResultSequence = 1, ItemDescription = "Tê PVC 25 mm", ItemUnit = "unidade", UnitPrice = 7m,
            SupplierName = "Fornecedor de teste", SupplierTaxId = "11222333000181", ResultDate = new(2026, 11, 1),
            Adequacy = new(12m, 20m, 5m, 15m, 5m, "Explicação legível de pontuação original.")
        };

        public static async Task<Fixture> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
            var project = await repository.CreateProjectAsync("Revisões técnicas externas");
            var line = await repository.CreateLineAsync(project.Id,
                new("Conexão de PVC para água fria com três vias de diâmetro nominal 25 milímetros", 100, "unidade", null, null));
            return new() { Database = database, Repository = repository, Line = line };
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
