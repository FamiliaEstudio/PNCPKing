using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class SweetCodeAndImportTests
{
    [Fact]
    public async Task SweetCodes_PersistOrderedLibraryAndEnabledState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteSweetCodeRepository(database.Repository.DatabasePath);

        var initial = await repository.LoadAsync();
        Assert.True(initial.Enabled);
        Assert.Empty(initial.Expressions);

        await repository.SaveAsync(false, [
            "Café -máquina \"pacote",
            "Café orgânico \"unidade"
        ]);
        var saved = await repository.LoadAsync();
        Assert.False(saved.Enabled);
        Assert.Equal([
            "Café -máquina \"pacote",
            "Café orgânico \"unidade"
        ], saved.Expressions);

        await repository.SetEnabledAsync(true);
        Assert.True((await repository.LoadAsync()).Enabled);
    }

    [Fact]
    public async Task SweetCodes_AreIncludedInTheNormalDatabaseBackup()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteSweetCodeRepository(database.Repository.DatabasePath);
        await repository.SaveAsync(true, ["Café \"pacote", "Açúcar -mascavo"]);
        var backupPath = Path.Combine(database.Directory, "sweet-backup.pncpking");
        var backup = new BackupService(database.Repository);
        await backup.ExportAsync(backupPath);
        await repository.SaveAsync(false, ["Alterado"]);

        await backup.ImportAsync(backupPath);

        var restored = await new SqliteSweetCodeRepository(database.Repository.DatabasePath).LoadAsync();
        Assert.True(restored.Enabled);
        Assert.Equal(["Café \"pacote", "Açúcar -mascavo"], restored.Expressions);
    }

    [Fact]
    public async Task Import_ReadsFirstVisibleSheetFromRowOneAndBrazilianNumbers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "entrada.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var hidden = workbook.Worksheets.Add("Instruções");
                hidden.Visibility = XLWorksheetVisibility.Hidden;
                var sheet = workbook.Worksheets.Add("Itens");
                sheet.Cell(1, 1).Value = "Café -máquina \"pacote \"unidade";
                sheet.Cell(1, 2).Value = "Café";
                sheet.Cell(1, 3).Value = 30000;
                sheet.Cell(1, 4).Value = "Pacote 500g";
                sheet.Cell(1, 5).Value = "30,50";
                sheet.Cell(1, 6).Value = 45m;
                sheet.Cell(1, 7).Value = 10;
                sheet.Cell(1, 8).Value = 7;
                workbook.SaveAs(path);
            }

            var document = await new QuotationWorkbookImportService().ReadAsync(path);

            var item = Assert.Single(document.Items);
            Assert.Equal(1, item.SourceRow);
            Assert.Equal("Café", item.OutputDescription);
            Assert.Equal(30000m, item.Quantity);
            Assert.Equal("Pacote 500g", item.Unit);
            Assert.Equal(30.50m, item.MinimumUnitPrice);
            Assert.Equal(45m, item.MaximumUnitPrice);
            Assert.Equal(10, item.BatchCount);
            Assert.Equal(7, item.RequestedBasketSize);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Import_AcceptsLegacyColumnsDefaultsEmptyHAndReportsInvalidHCell()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "legacy-a-g.xlsx");
        var emptyPath = Path.Combine(directory, "empty-h.xlsx");
        var invalidPath = Path.Combine(directory, "invalid-h.xlsx");
        try
        {
            CreateQuotationInputWorkbook(legacyPath, includeColumnH: false, basketSize: null);
            CreateQuotationInputWorkbook(emptyPath, includeColumnH: true, basketSize: null);
            CreateQuotationInputWorkbook(invalidPath, includeColumnH: true, basketSize: 11);

            Assert.Equal(
                3,
                Assert.Single((await new QuotationWorkbookImportService().ReadAsync(legacyPath)).Items)
                    .RequestedBasketSize);
            Assert.Equal(
                3,
                Assert.Single((await new QuotationWorkbookImportService().ReadAsync(emptyPath)).Items)
                    .RequestedBasketSize);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new QuotationWorkbookImportService().ReadAsync(invalidPath));
            Assert.Contains("Itens!H1", exception.Message);
            Assert.Contains("3 a 10", exception.Message);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Import_ReadsDownloadedSharedStringsAndMatchesTheResavedCopy()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PNCPKing.Tests",
            Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(
            root,
            "Dowload com espaço e acento");
        Directory.CreateDirectory(directory);
        var downloadedPath = Path.Combine(directory, "Planilha sem título.xlsx");
        var resavedPath = Path.Combine(directory, "Planilha regravada.xlsx");
        try
        {
            CreateProducerStyleWorkbook(downloadedPath);
            using (var workbook = new XLWorkbook(downloadedPath))
            {
                workbook.SaveAs(resavedPath);
            }

            var service = new QuotationWorkbookImportService();
            var downloaded = await service.ReadAsync(downloadedPath);
            var resaved = await service.ReadAsync(resavedPath);

            Assert.Equal(downloaded.Items, resaved.Items);
            Assert.Equal(2, downloaded.Items.Count);
            Assert.Equal("Café torrado", downloaded.Items[0].OutputDescription);
            Assert.Equal("Pacote 500g", downloaded.Items[0].Unit);
            Assert.Equal(30000m, downloaded.Items[0].Quantity);
            Assert.Equal(4, downloaded.Items[1].SourceRow);
            Assert.Equal(5, downloaded.Items[1].BatchCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Import_ReadsTheCachedValueOfAFormula()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fórmula baixada.xlsx");
        try
        {
            CreateProducerStyleWorkbook(path, useCachedFormula: true);

            var document = await new QuotationWorkbookImportService().ReadAsync(path);

            Assert.Equal(5, document.Items[1].BatchCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Import_ReportsEveryInvalidRowAndRejectsLegacyExcelExtension()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "invalid.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.Worksheets.Add("Itens");
                sheet.Cell(1, 1).Value = "-somente-negativo";
                sheet.Cell(1, 2).Value = "Inválido";
                sheet.Cell(1, 3).Value = 1;
                sheet.Cell(1, 4).Value = "unidade";
                sheet.Cell(1, 7).Value = 1;
                sheet.Cell(2, 1).Value = "cafe";
                sheet.Cell(2, 2).Value = "Café";
                sheet.Cell(2, 3).Value = 0;
                sheet.Cell(2, 4).Value = "unidade";
                sheet.Cell(2, 7).Value = 101;
                sheet.Cell(4, 1).Value = "cafe";
                sheet.Cell(4, 2).Value = "Café";
                sheet.Cell(4, 3).Value = 1;
                sheet.Cell(4, 4).Value = "unidade";
                sheet.Cell(4, 5).Value = "trinta";
                sheet.Cell(4, 7).Value = 1;
                workbook.SaveAs(path);
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new QuotationWorkbookImportService().ReadAsync(path));
            Assert.Contains("Linha 1", exception.Message);
            Assert.Contains("Linha 2", exception.Message);
            Assert.Contains("Itens!A1", exception.Message);
            Assert.Contains("Itens!C2", exception.Message);
            Assert.Contains("Itens!E4", exception.Message);
            Assert.Contains("\"trinta\"", exception.Message);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new QuotationWorkbookImportService().ReadAsync(Path.ChangeExtension(path, ".xls")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Import_IdentifiesTheSimplifiedResultWorkbook()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "resultado-cotação.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.Worksheets.Add("Cotação");
                sheet.Cell(1, 1).Value = "Café";
                sheet.Cell(2, 1).Value = "Fornecedor";
                sheet.Cell(2, 2).Value = "03.370.573/0001-03";
                sheet.Cell(2, 3).Value = "INCISO II";
                sheet.Cell(2, 4).Value = 30m;
                workbook.SaveAs(path);
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new QuotationWorkbookImportService().ReadAsync(path));

            Assert.Contains("planilha de resultado", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("colunas A:H", exception.Message);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void CreateProducerStyleWorkbook(string path, bool useCachedFormula = false)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sharedStringPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedStringPart.SharedStringTable = new SharedStringTable();

        var instructionsPart = workbookPart.AddNewPart<WorksheetPart>();
        instructionsPart.Worksheet = new Worksheet(new SheetData());
        var itemsPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        itemsPart.Worksheet = new Worksheet(sheetData);

        var strings = new[]
        {
            "Café -máquina \"pacote \"unidade",
            "Café\u00A0torrado",
            "Pacote\u202F500g",
            "Café gourmet -cápsula \"pacote",
            "Café Gourmet",
            "Pacote 500g"
        };
        foreach (var value in strings)
        {
            sharedStringPart.SharedStringTable.AppendChild(
                new SharedStringItem(new Text(value) { Space = SpaceProcessingModeValues.Preserve }));
        }

        sheetData.Append(
            new Row(
                SharedStringCell("A1", 0),
                SharedStringCell("B1", 1),
                NumberCell("C1", "30000"),
                SharedStringCell("D1", 2),
                NumberCell("E1", "30"),
                NumberCell("F1", "45"),
                NumberCell("G1", "5"))
            {
                RowIndex = 1
            });
        sheetData.Append(
            new Row(
                new Cell { CellReference = "A3" },
                new Cell { CellReference = "G3" })
            {
                RowIndex = 3
            });
        sheetData.Append(
            new Row(
                SharedStringCell("A4", 3),
                SharedStringCell("B4", 4),
                NumberCell("C4", "80000"),
                SharedStringCell("D4", 5),
                NumberCell("E4", "30,00", CellValues.String),
                NumberCell("F4", "45.00", CellValues.String),
                useCachedFormula
                    ? new Cell
                    {
                        CellReference = "G4",
                        CellFormula = new CellFormula("2+3"),
                        CellValue = new CellValue("5")
                    }
                    : NumberCell("G4", "5"))
            {
                RowIndex = 4
            });

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(instructionsPart),
                SheetId = 1,
                Name = "Instruções",
                State = SheetStateValues.Hidden
            },
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(itemsPart),
                SheetId = 2,
                Name = "Página1"
            });
        workbookPart.Workbook.Save();
    }

    private static void CreateQuotationInputWorkbook(
        string path,
        bool includeColumnH,
        int? basketSize)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Itens");
        sheet.Cell(1, 1).Value = "cafe";
        sheet.Cell(1, 2).Value = "Café";
        sheet.Cell(1, 3).Value = 10;
        sheet.Cell(1, 4).Value = "pacote";
        sheet.Cell(1, 7).Value = 1;
        if (includeColumnH && basketSize is not null)
        {
            sheet.Cell(1, 8).Value = basketSize.Value;
        }

        workbook.SaveAs(path);
    }

    private static Cell SharedStringCell(string reference, int index) =>
        new()
        {
            CellReference = reference,
            DataType = CellValues.SharedString,
            CellValue = new CellValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

    private static Cell NumberCell(string reference, string value, CellValues? type = null) =>
        new()
        {
            CellReference = reference,
            DataType = type,
            CellValue = new CellValue(value)
        };
}
