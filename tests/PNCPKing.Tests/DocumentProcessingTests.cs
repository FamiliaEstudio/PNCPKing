using System.IO.Compression;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using UglyToad.PdfPig.Writer;

namespace PNCPKing.Tests;

public sealed class DocumentProcessingTests
{
    [Fact]
    public void ManualSearch_DefaultsToThreeBatchesOfFiftyContracts()
    {
        Assert.Equal(3, ItemSearchDefaults.InitialBatchCount);
        Assert.Equal(150, ItemSearchDefaults.InitialBatchCount * ItemSearchDefaults.ContractsPerBatch);
    }

    [Fact]
    public void ContractKey_ParsesPortalUrlAndControlNumber()
    {
        Assert.True(PncpContractKey.TryParse(
            "11222333000181-1-000123/2026",
            "https://pncp.gov.br/app/editais/11222333000181/2026/123",
            out var fromPortal));
        Assert.NotNull(fromPortal);
        Assert.Equal("11222333000181", fromPortal.Cnpj);
        Assert.Equal(2026, fromPortal.PurchaseYear);
        Assert.Equal(123, fromPortal.PurchaseSequence);

        Assert.True(PncpContractKey.TryParse(
            "11222333000181-1-000123/2026",
            null,
            out var fromControlNumber));
        Assert.NotNull(fromControlNumber);
        Assert.Equal(123, fromControlNumber.PurchaseSequence);
    }

    [Fact]
    public void FlexibleMatcher_NormalizesAccentsPunctuationAndAuxiliaryWords()
    {
        var page = Page(
            "Fornecimento", "do", "CAFÉ-TORRADO", "e", "moído", "em", "pacotes");

        var occurrences = FlexiblePhraseMatcher.Find(
            "cafe torrado moido pacotes",
            page);

        var occurrence = Assert.Single(occurrences);
        Assert.Equal([2, 4, 6], occurrence.WordIndexes);
        Assert.Empty(FlexiblePhraseMatcher.Find("moído café", page));

        var overlapping = FlexiblePhraseMatcher.Find(
            "café café",
            Page("cafe", "café", "cafe"));
        Assert.Single(overlapping);
    }

    [Fact]
    public async Task DocumentService_ExtractsDeduplicatesCachesAndConsolidatesZip()
    {
        var root = CreateTemporaryFolder();
        var outsidePath = Path.Combine(
            Path.GetDirectoryName(root)!,
            $"outside-{Path.GetFileName(root)}.pdf");
        try
        {
            var pdf = BuildPdf("Aquisicao de cafe torrado e moido");
            var archive = BuildZip(
                ("../outside.pdf", pdf),
                ("documentos/edital.pdf", pdf));
            var client = new FakeDocumentClient(archive, "documentos.zip");
            var service = new ContractDocumentService(client, ContractDataFolder(root));
            var contract = Contract();

            var first = await service.PrepareAsync(contract);
            var second = await service.PrepareAsync(contract);
            var destination = Path.Combine(root, "consolidado.pdf");
            var consolidated = await service.CreateConsolidatedPdfAsync(contract, destination);

            Assert.Single(first.Pdfs);
            Assert.Single(second.Pdfs);
            Assert.Single(consolidated.Pdfs);
            Assert.Equal(1, client.DownloadCalls);
            Assert.Equal(3, client.ListCalls);
            Assert.True(File.Exists(destination));
            Assert.False(File.Exists(outsidePath));
            Assert.All(
                first.Pdfs,
                item => Assert.StartsWith(
                    Path.GetFullPath(ContractDataFolder(root)),
                    Path.GetFullPath(item.LocalPath),
                    StringComparison.OrdinalIgnoreCase));

            var removed = await service.ClearCacheAsync();
            Assert.True(removed > 0);
            Assert.False(Directory.Exists(Path.Combine(ContractDataFolder(root), "document-cache")));
            Assert.True(File.Exists(destination));
        }
        finally
        {
            DeleteDirectory(root);
            if (File.Exists(outsidePath))
            {
                File.Delete(outsidePath);
            }
        }
    }

    [Fact]
    public async Task RelevantPages_CombinesExpressionsCopiesPagesOnceAndHighlightsDistinctWords()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(
                sourcePdf,
                BuildPdfPages(
                    "PAGINA UM COM ACUCAR",
                    "PAGINA DOIS COM CAFE TORRADO E ACUCAR",
                    "PAGINA TRES SEM A REFERENCIA"));
            var cached = Cached(sourcePdf);
            var index = new DocumentTextIndex
            {
                PdfSha256 = cached.Sha256,
                SourcePath = sourcePdf,
                Pages =
                [
                    IndexedPage(1, "acucar"),
                    IndexedPage(2, "cafe", "torrado", "acucar") with
                    {
                        Source = DocumentTextSource.Ocr
                    },
                    IndexedPage(3, "pagina", "tres")
                ]
            };
            var destination = Path.Combine(root, "relevantes.pdf");
            var service = new ContractRelevantPageService(
                new FakeContractDocumentService(cached),
                new StaticTextIndexService(index));

            var result = await service.CreateAsync(
                Contract(),
                [
                    " café torrado ",
                    "cafe",
                    "AÇÚCAR",
                    "café-torrado",
                    " ",
                    "produto inexistente"
                ],
                destination);

            Assert.Equal(1, result.MatchedPdfCount);
            Assert.Equal(2, result.MatchedPageCount);
            Assert.Equal(4, result.OccurrenceCount);
            Assert.Collection(
                result.Expressions,
                expression =>
                {
                    Assert.Equal("café torrado", expression.Expression);
                    Assert.Equal(1, expression.MatchedPageCount);
                    Assert.Equal(1, expression.OccurrenceCount);
                },
                expression =>
                {
                    Assert.Equal("cafe", expression.Expression);
                    Assert.Equal(1, expression.MatchedPageCount);
                    Assert.Equal(1, expression.OccurrenceCount);
                },
                expression =>
                {
                    Assert.Equal("AÇÚCAR", expression.Expression);
                    Assert.Equal(2, expression.MatchedPageCount);
                    Assert.Equal(2, expression.OccurrenceCount);
                },
                expression =>
                {
                    Assert.Equal("produto inexistente", expression.Expression);
                    Assert.Equal(0, expression.MatchedPageCount);
                    Assert.Equal(0, expression.OccurrenceCount);
                });
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("produto inexistente", StringComparison.Ordinal));
            Assert.Equal(destination, result.OutputPath);
            Assert.True(File.Exists(destination));
            using var output = UglyToad.PdfPig.PdfDocument.Open(destination);
            Assert.Equal(2, output.NumberOfPages);
            Assert.Contains(
                "PAGINA UM COM ACUCAR",
                output.GetPage(1).Text,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "PAGINA DOIS COM CAFE TORRADO E ACUCAR",
                output.GetPage(2).Text,
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(output.GetPage(1).Operations.OfType<AppendRectangle>());
            Assert.Equal(3, output.GetPage(2).Operations.OfType<AppendRectangle>().Count());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task RelevantPages_PreservesSearchableTextAndAlignsHighlightsOnCroppedRotatedPages(
        int rotation)
    {
        var root = CreateTemporaryFolder();
        try
        {
            var sourcePdf = Path.Combine(root, $"rotacao-{rotation}.pdf");
            using (var input = new MemoryStream(
                       BuildPdf("REFERENCIA ROTACIONADA CAFE TORRADO DOCUMENTO TESTE")))
            using (var source = PdfSharp.Pdf.IO.PdfReader.Open(
                       input,
                       PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
            {
                source.Pages[0].CropBox = new PdfSharp.Pdf.PdfRectangle(
                    new PdfSharp.Drawing.XPoint(20, 20),
                    new PdfSharp.Drawing.XSize(555, 802));
                source.Pages[0].Rotate = rotation;
                source.Save(sourcePdf);
            }

            var cached = Cached(sourcePdf);
            var rasterizer = new CountingRasterizer();
            var indexes = new PdfTextIndexService(
                rasterizer,
                new CountingOcrService([]));
            var index = await indexes.BuildAsync(cached);
            var destination = Path.Combine(root, $"relevantes-{rotation}.pdf");
            var service = new ContractRelevantPageService(
                new FakeContractDocumentService(cached),
                indexes);

            var result = await service.CreateAsync(
                Contract(),
                ["cafe torrado"],
                destination);

            Assert.Equal(1, result.MatchedPageCount);
            Assert.Equal(0, rasterizer.Calls);
            using var output = UglyToad.PdfPig.PdfDocument.Open(destination);
            var page = output.GetPage(1);
            Assert.Equal(index.Pages[0].Width, page.Width, 2);
            Assert.Equal(index.Pages[0].Height, page.Height, 2);
            var relevantWords = NearestNeighbourWordExtractor.Instance
                .GetWords(page.Letters)
                .Where(word => word.Text.Equals("CAFE", StringComparison.OrdinalIgnoreCase) ||
                               word.Text.Equals("TORRADO", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(2, relevantWords.Length);
            var highlightRectangles = page.Paths
                .Where(path => path.IsFilled && path.IsStroked)
                .Select(path => path.GetBoundingRectangle())
                .OfType<PdfRectangle>()
                .ToArray();
            Assert.Equal(2, highlightRectangles.Length);
            Assert.All(
                relevantWords,
                word => Assert.True(
                    highlightRectangles.Any(rectangle => Intersects(rectangle, word.BoundingBox)),
                    $"O realce não cruzou {word.Text} em {word.BoundingBox}; " +
                    $"realces: {string.Join(", ", highlightRectangles)}; " +
                    $"índice: {string.Join(", ", index.Pages[0].Words.Select(indexed => $"{indexed.Text}={indexed.Bounds}"))}"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RelevantPages_ReportsEveryMissingExpressionWithoutCreatingPdf()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(sourcePdf, BuildPdf("DOCUMENTO SEM RESULTADOS"));
            var cached = Cached(sourcePdf);
            var index = new DocumentTextIndex
            {
                PdfSha256 = cached.Sha256,
                SourcePath = sourcePdf,
                Pages = [IndexedPage(1, "documento", "sem", "resultados")]
            };
            var destination = Path.Combine(root, "sem-resultados.pdf");
            var service = new ContractRelevantPageService(
                new FakeContractDocumentService(cached),
                new StaticTextIndexService(index));

            var result = await service.CreateAsync(
                Contract(),
                ["cafe", "acucar"],
                destination);

            Assert.Null(result.OutputPath);
            Assert.Equal(2, result.Expressions.Count);
            Assert.All(result.Expressions, expression => Assert.Equal(0, expression.OccurrenceCount));
            Assert.Contains(result.Warnings, warning => warning.Contains("cafe", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, warning => warning.Contains("acucar", StringComparison.Ordinal));
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RelevantPages_CancellationRemovesPartialOutput()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(sourcePdf, BuildPdf("CAFE TORRADO DOCUMENTO"));
            var cached = Cached(sourcePdf);
            var index = new DocumentTextIndex
            {
                PdfSha256 = cached.Sha256,
                SourcePath = sourcePdf,
                Pages = [IndexedPage(1, "cafe", "torrado")]
            };
            var destination = Path.Combine(root, "cancelado.pdf");
            var service = new ContractRelevantPageService(
                new FakeContractDocumentService(cached),
                new StaticTextIndexService(index));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => service.CreateAsync(
                    Contract(),
                    ["cafe torrado"],
                    destination,
                    cancellationToken: cancellation.Token));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DocumentService_StopsAtNestedArchiveLimit()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var pdf = BuildPdf("Documento profundamente compactado");
            var level3 = BuildZip(("documento.pdf", pdf));
            var level2 = BuildZip(("nivel3.zip", level3));
            var level1 = BuildZip(("nivel2.zip", level2));
            var service = new ContractDocumentService(
                new FakeDocumentClient(level1, "nivel1.zip"),
                ContractDataFolder(root));

            var result = await service.PrepareAsync(Contract());

            Assert.Empty(result.Pdfs);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("níveis compactados", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DocumentService_InvalidatesAChangedCachedSourceBySha256()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var client = new FakeDocumentClient(BuildPdf("documento valido"), "edital.pdf");
            var dataFolder = ContractDataFolder(root);
            var service = new ContractDocumentService(client, dataFolder);

            await service.PrepareAsync(Contract());
            var cachedSource = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(dataFolder, "document-cache"),
                "*.bin",
                SearchOption.AllDirectories));
            await File.WriteAllBytesAsync(cachedSource, "%PDF-corrompido"u8.ToArray());

            var refreshed = await service.PrepareAsync(Contract());

            Assert.Equal(2, client.DownloadCalls);
            Assert.Single(refreshed.Pdfs);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TextIndex_UsesNativeWordsWithoutOcr()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var path = Path.Combine(root, "native.pdf");
            await File.WriteAllBytesAsync(path, BuildPdf(
                "Fornecimento de cafe torrado e moido em embalagem de quinhentos gramas"));
            var rasterizer = new CountingRasterizer();
            var ocr = new CountingOcrService([]);
            var service = new PdfTextIndexService(rasterizer, ocr);

            var index = await service.BuildAsync(Cached(path));

            var page = Assert.Single(index.Pages);
            Assert.Equal(DocumentTextSource.Native, page.Source);
            Assert.NotEmpty(page.Words);
            Assert.NotEmpty(page.Blocks);
            Assert.Equal(0, rasterizer.Calls);
            Assert.Equal(0, ocr.Calls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TextIndex_UsesOcrOnlyWhenNativeLayerIsNotUsableAndReusesIndex()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var path = Path.Combine(root, "scan.pdf");
            await File.WriteAllBytesAsync(path, BuildPdf(null));
            var rasterizer = new CountingRasterizer();
            var ocr = new CountingOcrService(
            [
                new DocumentWord(
                    "conteúdo",
                    new DocumentRectangle(10, 10, 50, 12),
                    0),
                new DocumentWord(
                    "digitalizado",
                    new DocumentRectangle(65, 10, 70, 12),
                    0)
            ]);
            var service = new PdfTextIndexService(rasterizer, ocr);

            var first = await service.BuildAsync(Cached(path));
            var second = await service.BuildAsync(Cached(path));

            Assert.Equal(DocumentTextSource.Ocr, Assert.Single(first.Pages).Source);
            Assert.Equal(DocumentTextSource.Ocr, Assert.Single(second.Pages).Source);
            Assert.Equal(1, rasterizer.Calls);
            Assert.Equal(1, ocr.Calls);
            Assert.True(File.Exists(path + ".index.json"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TextIndex_RetriesOcrAtLowerResolutionAndCachesSuccessfulResult()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var path = Path.Combine(root, "scan-instavel.pdf");
            await File.WriteAllBytesAsync(path, BuildPdf(null));
            var rasterizer = new CountingRasterizer();
            var ocr = new FlakyOcrService(
            [
                new DocumentWord(
                    "limpeza",
                    new DocumentRectangle(10, 10, 50, 12),
                    0)
            ]);
            var service = new PdfTextIndexService(rasterizer, ocr);

            var first = await service.BuildAsync(Cached(path));
            var second = await service.BuildAsync(Cached(path));

            Assert.Equal(DocumentTextSource.Ocr, Assert.Single(first.Pages).Source);
            Assert.Equal("limpeza", Assert.Single(first.Pages[0].Words).Text);
            Assert.Empty(first.Warnings);
            Assert.Empty(second.Warnings);
            Assert.Equal(2, rasterizer.Calls);
            Assert.Equal([300, 200], rasterizer.Dpis);
            Assert.Equal(2, ocr.Calls);
            Assert.True(File.Exists(path + ".index.json"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TextIndex_PreservesOtherPagesAndReportsInnerExceptionWhenOcrKeepsFailing()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var path = Path.Combine(root, "edital-parcial.pdf");
            await File.WriteAllBytesAsync(
                path,
                BuildPdfPages(
                    "SERVICO DE LIMPEZA CONTINUADA COM FORNECIMENTO DE MATERIAIS",
                    null));
            var rasterizer = new CountingRasterizer();
            var ocr = new ThrowingOcrService();
            var service = new PdfTextIndexService(rasterizer, ocr);

            var first = await service.BuildAsync(Cached(path));

            Assert.Equal(2, first.Pages.Count);
            Assert.NotEmpty(first.Pages[0].Words);
            Assert.Empty(first.Pages[1].Words);
            var warning = Assert.Single(first.Warnings);
            Assert.Contains("Página 2", warning, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.Contains("falha interna do OCR", warning, StringComparison.Ordinal);
            Assert.Equal(2, rasterizer.Calls);
            Assert.Equal([300, 200], rasterizer.Dpis);
            Assert.Equal(2, ocr.Calls);
            Assert.False(File.Exists(path + ".index.json"));

            var second = await service.BuildAsync(Cached(path));

            Assert.Equal(2, second.Pages.Count);
            Assert.Equal(4, rasterizer.Calls);
            Assert.Equal(4, ocr.Calls);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RelevantPages_PropagatesIndexWarningAndKeepsMatchingGoodPages()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(
                sourcePdf,
                BuildPdf("SERVICO DE LIMPEZA CONTINUADA"));
            var cached = Cached(sourcePdf);
            var index = new DocumentTextIndex
            {
                PdfSha256 = cached.Sha256,
                SourcePath = sourcePdf,
                Pages = [IndexedPage(1, "servico", "de", "limpeza", "continuada")],
                Warnings =
                [
                    "Página 2: não foi possível executar o OCR; as demais páginas foram preservadas."
                ]
            };
            var destination = Path.Combine(root, "relevantes.pdf");
            var service = new ContractRelevantPageService(
                new FakeContractDocumentService(cached),
                new StaticTextIndexService(index));

            var result = await service.CreateAsync(
                Contract(),
                ["serviço", "limpeza"],
                destination);

            Assert.Equal(1, result.MatchedPageCount);
            Assert.NotNull(result.OutputPath);
            Assert.Contains(
                result.Warnings,
                warning =>
                    warning.Contains("Página 2", StringComparison.Ordinal) &&
                    warning.Contains("OCR", StringComparison.Ordinal));
            Assert.True(File.Exists(destination));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void NativeTextThreshold_IsExactlyTwentyAlphanumericCharacters(
        int characters,
        bool expected)
    {
        var words = new[]
        {
            new DocumentWord(
                new string('a', characters),
                new DocumentRectangle(0, 0, 100, 10),
                0)
        };

        Assert.Equal(expected, PdfTextIndexService.HasUsableNativeText(words));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void NativeTextThreshold_RequiresAtLeastSeventyPercentPrintableCharacters(
        int controlCharacters,
        bool expected)
    {
        var words = new[]
        {
            new DocumentWord(
                new string('a', 20) + new string('\u0001', controlCharacters),
                new DocumentRectangle(0, 0, 100, 10),
                0)
        };

        Assert.Equal(expected, PdfTextIndexService.HasUsableNativeText(words));
    }

    [Fact]
    public void NativeOcrResolver_AvoidsNullPathAndFindsSingleFileExtractionRoot()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var extractionRoot = Path.Combine(root, "extracao");
            var platformFolder = Path.Combine(extractionRoot, "x64");
            Directory.CreateDirectory(platformFolder);
            File.WriteAllBytes(Path.Combine(platformFolder, "tesseract50.dll"), [1]);
            File.WriteAllBytes(Path.Combine(platformFolder, "leptonica-1.82.0.dll"), [1]);
            var searchDirectories = string.Join(
                Path.PathSeparator,
                string.Empty,
                Path.Combine(root, "inexistente"),
                extractionRoot);

            var resolved = NativeOcrLibraryResolver.FindTesseractRoot(
                searchDirectories,
                applicationBaseDirectory: null,
                is64BitProcess: true);

            Assert.Equal(Path.GetFullPath(extractionRoot), resolved);
            Assert.Null(NativeOcrLibraryResolver.FindTesseractRoot(
                nativeSearchDirectories: null,
                applicationBaseDirectory: null,
                is64BitProcess: true));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task EvidenceReport_OrdersReferenceAndWritesFullOccurrencePage()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var destination = Path.Combine(root, "evidencias.pdf");
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(sourcePdf, BuildPdf("cafe torrado"));
            var reference = Reference();
            var report = Report(reference);
            var rasterizer = new EvidenceRasterizer();
            var service = new QuotationEvidenceExportService(
                new FakeContractDocumentService(Cached(sourcePdf)),
                new FakeTextIndexService(reference),
                rasterizer);

            var result = await service.ExportAsync(destination, report);

            Assert.Equal(1, result.Items);
            Assert.Equal(1, result.References);
            Assert.Equal(1, result.Occurrences);
            Assert.Empty(result.Warnings);
            Assert.True(File.Exists(destination));
            Assert.Equal(300, rasterizer.LastDpi);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(destination);
            Assert.Equal(2, pdf.NumberOfPages);
            var reportImage = Assert.Single(pdf.GetPage(2).GetImages());
            Assert.True(reportImage.TryGetPng(out var reportPng));
            using var reportBitmap = SKBitmap.Decode(reportPng);
            Assert.NotNull(reportBitmap);
            Assert.Equal(2480, reportBitmap.Width);
            Assert.Equal(3508, reportBitmap.Height);
            Assert.Contains(
                reportBitmap.Pixels,
                color => color.Red > 240 && color.Blue > 240 && color.Green < 20);
            Assert.Contains(
                reportBitmap.Pixels,
                color => color.Blue > 240 && color.Green > 240 && color.Red < 20);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task EvidenceReport_FallsBackToBasicItemIdentityWhenFullDescriptionsAreAbsent()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var destination = Path.Combine(root, "evidencias-basicas.pdf");
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(sourcePdf, BuildPdf("tesoura"));
            var reference = Reference() with
            {
                ItemDescription = "Tesoura profissional de aço inoxidável com cabo anatômico"
            };
            var index = new DocumentTextIndex
            {
                PdfSha256 = Cached(sourcePdf).Sha256,
                SourcePath = sourcePdf,
                Pages = [IndexedPage(1, "tesoura")]
            };
            var service = new QuotationEvidenceExportService(
                new FakeContractDocumentService(Cached(sourcePdf)),
                new StaticTextIndexService(index),
                new EvidenceRasterizer());

            var result = await service.ExportAsync(
                destination,
                Report(
                    reference,
                    "Tesoura escolar sem ponta, lâmina de 13 centímetros e cabo em polipropileno"));

            Assert.Equal(1, result.References);
            Assert.Equal(1, result.Occurrences);
            Assert.True(File.Exists(destination));
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(destination);
            Assert.Equal(2, pdf.NumberOfPages);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task EvidenceReport_PreservesPartialPdfWhenCancelled()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var destination = Path.Combine(root, "cancelado.pdf");
            var sourcePdf = Path.Combine(root, "documento.pdf");
            await File.WriteAllBytesAsync(sourcePdf, BuildPdf("cafe torrado"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var reference = Reference();
            var service = new QuotationEvidenceExportService(
                new FakeContractDocumentService(Cached(sourcePdf)),
                new FakeTextIndexService(reference),
                new EvidenceRasterizer());

            var result = await service.ExportAsync(
                destination,
                Report(reference),
                cancellationToken: cancellation.Token);

            Assert.True(File.Exists(destination));
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("interrompida", StringComparison.OrdinalIgnoreCase));
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(destination);
            Assert.Equal(2, pdf.NumberOfPages);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static DocumentPageIndex Page(params string[] values) =>
        new()
        {
            PageNumber = 1,
            Width = 600,
            Height = 800,
            Source = DocumentTextSource.Native,
            Words = values
                .Select((value, index) => new DocumentWord(
                    value,
                    new DocumentRectangle(index * 50, 100, 45, 12),
                    0))
                .ToArray()
        };

    private static byte[] BuildPdf(string? text)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            page.AddText(text, 12, new PdfPoint(40, 780), font);
        }

        return builder.Build();
    }

    private static byte[] BuildPdfPages(params string?[] texts)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in texts)
        {
            var page = builder.AddPage(595, 842);
            if (!string.IsNullOrWhiteSpace(text))
            {
                page.AddText(text, 12, new PdfPoint(40, 780), font);
            }
        }

        return builder.Build();
    }

    private static DocumentPageIndex IndexedPage(int pageNumber, params string[] words) =>
        new()
        {
            PageNumber = pageNumber,
            Width = 595,
            Height = 842,
            Source = DocumentTextSource.Native,
            Words = words
                .Select((word, index) => new DocumentWord(
                    word,
                    new DocumentRectangle(40 + index * 55, 50, 50, 12),
                    0))
                .ToArray()
        };

    private static bool Intersects(PdfRectangle first, PdfRectangle second)
    {
        const double tolerance = 0.01;
        var firstX = new[]
        {
            first.TopLeft.X, first.TopRight.X, first.BottomLeft.X, first.BottomRight.X
        };
        var firstY = new[]
        {
            first.TopLeft.Y, first.TopRight.Y, first.BottomLeft.Y, first.BottomRight.Y
        };
        var secondX = new[]
        {
            second.TopLeft.X, second.TopRight.X, second.BottomLeft.X, second.BottomRight.X
        };
        var secondY = new[]
        {
            second.TopLeft.Y, second.TopRight.Y, second.BottomLeft.Y, second.BottomRight.Y
        };
        return firstX.Min() <= secondX.Max() + tolerance &&
               firstX.Max() + tolerance >= secondX.Min() &&
               firstY.Min() <= secondY.Max() + tolerance &&
               firstY.Max() + tolerance >= secondY.Min();
    }

    private static byte[] BuildZip(params (string Name, byte[] Bytes)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name, CompressionLevel.SmallestSize);
                using var stream = entry.Open();
                stream.Write(file.Bytes);
            }
        }

        return output.ToArray();
    }

    private static CachedPdfDocument Cached(string path) =>
        new()
        {
            LocalPath = path,
            Sha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant(),
            DocumentSequence = 1,
            DocumentTitle = Path.GetFileName(path)
        };

    private static QuotationReference Reference() =>
        new()
        {
            Id = "reference-1",
            LineId = Guid.NewGuid(),
            ContractId = Contract().PncpId,
            ItemNumber = 7,
            ResultSequence = 1,
            SupplierName = "Fornecedor de teste",
            SupplierTaxId = "11222333000181",
            UnitPrice = 42.50m,
            ItemDescription = "cafe torrado",
            ItemUnit = "pacote",
            PortalUrl = "https://pncp.gov.br/app/editais/11222333000181/2026/123",
            State = QuotationReferenceState.Eligible
        };

    private static QuotationProjectReport Report(
        QuotationReference reference,
        string description = "cafe torrado")
    {
        var projectId = Guid.NewGuid();
        var line = new QuotationLine
        {
            Id = reference.LineId,
            ProjectId = projectId,
            Description = description,
            RequestedQuantity = 1,
            RequestedUnit = "pacote",
            SelectedBasketKey = "automatic:test",
            SelectionConfirmed = true
        };
        var basket = new QuotationBasket
        {
            Key = "automatic:test",
            References = [reference],
            AveragePrice = reference.UnitPrice,
            MinimumPrice = reference.UnitPrice,
            MaximumPrice = reference.UnitPrice,
            MaximumDeviationPercent = 0,
            Score = 100,
            IsRecommended = true
        };
        var analysis = new QuotationLineAnalysis(
            line,
            [reference],
            [basket],
            1,
            1,
            0,
            0,
            1);
        return new QuotationProjectReport(
            new QuotationProject(
                projectId,
                "Projeto de evidências",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            [analysis]);
    }

    private static PncpContractKey Contract() =>
        new("11222333000181-1-000123/2026", "11222333000181", 2026, 123);

    private static string ContractDataFolder(string root) =>
        Path.Combine(root, "data");

    private static string CreateTemporaryFolder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PNCPKing.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeDocumentClient(byte[] content, string fileName) : IPncpDocumentClient
    {
        public int ListCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<IReadOnlyList<PncpDocumentDescriptor>> ListDocumentsAsync(
            PncpContractKey contract,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<PncpDocumentDescriptor>>(
            [
                new PncpDocumentDescriptor
                {
                    Sequence = 1,
                    Title = fileName,
                    DownloadUri = $"https://example.test/{fileName}"
                }
            ]);
        }

        public Task<PncpDocumentContent> DownloadDocumentAsync(
            PncpContractKey contract,
            PncpDocumentDescriptor document,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult(new PncpDocumentContent(
                content,
                "application/octet-stream",
                fileName));
        }
    }

    private sealed class CountingRasterizer : IPdfPageRasterizer
    {
        public int Calls { get; private set; }
        public List<int> Dpis { get; } = [];

        public Task<RenderedPdfPage> RenderAsync(
            string pdfPath,
            int pageNumber,
            int dpi = 300,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Dpis.Add(dpi);
            return Task.FromResult(new RenderedPdfPage([1, 2, 3], 100, 100, 595, 842));
        }
    }

    private sealed class CountingOcrService(IReadOnlyList<DocumentWord> words) : IOcrService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
            RenderedPdfPage page,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(words);
        }
    }

    private sealed class FlakyOcrService(IReadOnlyList<DocumentWord> words) : IOcrService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
            RenderedPdfPage page,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new System.Reflection.TargetInvocationException(
                    new InvalidOperationException("falha interna transitória do OCR"));
            }

            return Task.FromResult(words);
        }
    }

    private sealed class ThrowingOcrService : IOcrService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DocumentWord>> RecognizeAsync(
            RenderedPdfPage page,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new System.Reflection.TargetInvocationException(
                new InvalidOperationException("falha interna do OCR"));
        }
    }

    private sealed class FakeContractDocumentService(CachedPdfDocument pdf) : IContractDocumentService
    {
        public Task<DocumentBundleResult> PrepareAsync(
            PncpContractKey contract,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DocumentBundleResult
            {
                Contract = contract,
                Pdfs = [pdf]
            });

        public Task<DocumentBundleResult> CreateConsolidatedPdfAsync(
            PncpContractKey contract,
            string destinationPath,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ClearCacheAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }

    private sealed class FakeTextIndexService(QuotationReference reference) : IPdfTextIndexService
    {
        public Task<DocumentTextIndex> BuildAsync(
            CachedPdfDocument pdf,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DocumentTextIndex
            {
                PdfSha256 = pdf.Sha256,
                SourcePath = pdf.LocalPath,
                Pages =
                [
                    new DocumentPageIndex
                    {
                        PageNumber = 1,
                        Width = 100,
                        Height = 100,
                        Source = DocumentTextSource.Native,
                        Words =
                        [
                            new DocumentWord("cafe", new DocumentRectangle(10, 40, 20, 10), 0),
                            new DocumentWord("torrado", new DocumentRectangle(35, 40, 30, 10), 0),
                            new DocumentWord(
                                reference.SupplierName,
                                new DocumentRectangle(10, 55, 50, 10),
                                1)
                        ]
                    }
                ]
            });
    }

    private sealed class StaticTextIndexService(DocumentTextIndex index) : IPdfTextIndexService
    {
        public Task<DocumentTextIndex> BuildAsync(
            CachedPdfDocument pdf,
            IProgress<DocumentProcessingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(index);
    }

    private sealed class EvidenceRasterizer : IPdfPageRasterizer
    {
        private static readonly byte[] FullPagePng = CreateFullPagePng();
        public int LastDpi { get; private set; }

        public Task<RenderedPdfPage> RenderAsync(
            string pdfPath,
            int pageNumber,
            int dpi = 300,
            CancellationToken cancellationToken = default)
        {
            LastDpi = dpi;
            return Task.FromResult(new RenderedPdfPage(FullPagePng, 100, 100, 100, 100));
        }

        private static byte[] CreateFullPagePng()
        {
            using var bitmap = new SKBitmap(
                100,
                100,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var top = new SKPaint { Color = SKColors.Magenta };
            using var bottom = new SKPaint { Color = SKColors.Cyan };
            canvas.DrawRect(0, 0, 100, 12, top);
            canvas.DrawRect(0, 88, 100, 12, bottom);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
    }
}
