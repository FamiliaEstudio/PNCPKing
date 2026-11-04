using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;

namespace PNCPKing.Infrastructure.Services;

// XLSX shared strings and the (potentially large) sparse triage are staged on disk.
// No whole worksheet/shared-string table is retained in managed memory.
internal sealed class QuotationAiWorkbookReader : IDisposable
{
    private readonly SpreadsheetDocument _document;
    private readonly string _temporaryPath;
    private readonly Dictionary<int, string> _cache = [];
    public SqliteConnection Connection { get; }
    public SqliteTransaction? Transaction { get; set; }

    public QuotationAiWorkbookReader(string path, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O guia e a triagem devem ser arquivos .xlsx.");
        using (var zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Sum(entry => entry.Length) > 1_073_741_824L)
                throw new InvalidDataException("A planilha excede 1 GiB descompactado. Divida a triagem em lotes.");
        }
        _temporaryPath = Path.Combine(Path.GetTempPath(), "pncpking-ai-workbook-" + Guid.NewGuid().ToString("N") + ".sqlite");
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _temporaryPath, Pooling = false }.ToString());
        _document = SpreadsheetDocument.Open(path, false);
        try
        {
            if (_document.WorkbookPart is not { } workbook || workbook.VbaProjectPart is not null ||
                workbook.ExternalRelationships.Any())
                throw new InvalidDataException("Use uma planilha simples, sem macros ou vínculos externos.");
            Connection.Open();
            using var setup = Connection.CreateCommand();
            setup.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048; CREATE TABLE strings(id INTEGER PRIMARY KEY,value TEXT NOT NULL);";
            setup.ExecuteNonQuery();
            using var transaction = Connection.BeginTransaction();
            using var insert = Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO strings(id,value) VALUES($id,$value);";
            insert.Parameters.Add("$id", SqliteType.Integer);
            insert.Parameters.Add("$value", SqliteType.Text);
            if (workbook.SharedStringTablePart is { } shared)
            {
                using var reader = OpenXmlReader.Create(shared);
                var index = 0;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement) continue;
                    var text = ((SharedStringItem)reader.LoadCurrentElement()!).InnerText;
                    if (text.Length > 262_144) throw new InvalidDataException("Uma célula da planilha é excessivamente longa.");
                    insert.Parameters["$id"].Value = index++;
                    insert.Parameters["$value"].Value = text;
                    insert.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        catch { Dispose(); throw; }
    }

    public bool HasWorksheet(string sheetName) =>
        _document.WorkbookPart!.Workbook.Sheets?.Elements<Sheet>().Any(sheet => sheet.Name?.Value == sheetName) == true;

    public IEnumerable<string[]> ReadRows(string sheetName, CancellationToken cancellationToken)
    {
        var workbook = _document.WorkbookPart!;
        var sheets = workbook.Workbook.Sheets?.Elements<Sheet>().Where(sheet => sheet.Name?.Value == sheetName).ToArray() ?? [];
        if (sheets.Length != 1 || sheets[0].Id?.Value is not { } relationshipId ||
            workbook.GetPartById(relationshipId) is not WorksheetPart part)
            throw new InvalidDataException($"A planilha deve conter uma única aba {sheetName}.");
        using var reader = OpenXmlReader.Create(part);
        var count = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            if (++count > 1_048_576) throw new InvalidDataException("A planilha excede o limite de linhas do Excel.");
            var row = (Row)reader.LoadCurrentElement()!;
            var values = new string[16];
            Array.Fill(values, string.Empty);
            var implicitIndex = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                if (cell.CellFormula is not null)
                    throw new InvalidDataException($"{sheetName}!{cell.CellReference}: fórmulas não são aceitas no intercâmbio com a IA.");
                var column = cell.CellReference?.Value is { } cellReference ? ColumnIndex(cellReference) : implicitIndex;
                implicitIndex = column + 1;
                if (column is < 0 or >= 16)
                {
                    if (cell.InnerText.Length != 0) throw new InvalidDataException($"{sheetName}: coluna inesperada.");
                    continue;
                }
                var text = cell.DataType?.Value == CellValues.InlineString
                    ? cell.InlineString?.InnerText ?? string.Empty : cell.CellValue?.InnerText ?? string.Empty;
                if (cell.DataType?.Value == CellValues.SharedString)
                {
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        throw new InvalidDataException("Índice de texto inválido na planilha.");
                    text = ReadString(id);
                }
                if (text.Length > 262_144) throw new InvalidDataException("Uma célula da planilha é excessivamente longa.");
                values[column] = text.Trim();
            }
            if (values.Any(value => value.Length != 0)) yield return values;
        }
    }

    private string ReadString(int id)
    {
        if (_cache.TryGetValue(id, out var text)) return text;
        using var read = Connection.CreateCommand();
        read.Transaction = Transaction;
        read.CommandText = "SELECT value FROM strings WHERE id = $id;";
        read.Parameters.AddWithValue("$id", id);
        text = read.ExecuteScalar() as string ?? throw new InvalidDataException("Texto compartilhado ausente na planilha.");
        if (_cache.Count >= 512) _cache.Clear();
        _cache[id] = text;
        return text;
    }

    private static int ColumnIndex(string reference)
    {
        var column = 0;
        foreach (var value in reference)
        {
            if (value is < 'A' or > 'Z') break;
            column = checked(column * 26 + value - 'A' + 1);
        }
        return column - 1;
    }

    public void Dispose()
    {
        _document.Dispose();
        Connection.Dispose();
        if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath);
    }
}
