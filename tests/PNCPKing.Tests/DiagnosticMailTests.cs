using PNCPKing.App.Services;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class DiagnosticMailTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void SelectedIncidentIncludesExactSchemaAndDraftKeepsTheSameAttachment(uint status)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var log = new AppDiagnosticLog(null, directory.FullName);
            log.Info("test", "unrelated-session-data");
            var failure = new InvalidDataException("Objeto incompatível");
            const string sql = "CREATE TRIGGER x AFTER INSERT ON t BEGIN\r\n SELECT 'á',  2; END";
            failure.Data[BackupService.TransferSchemaDiagnosticKey] = "origem=banco local\nobjeto=trigger:x\n" + sql;
            var incident = log.Error("backup", "Falha na importação.", new IOException("Importação interrompida", failure));
            Assert.Contains(sql, File.ReadAllText(log.FilePath));
            Assert.Equal(incident, log.LastError);
            log.Error("other", "later-unrelated-error", new Exception("another operation"));
            var attempted = 0;
            var result = DiagnosticMail.Submit(directory.FullName, incident, new Version(1, 2, 48, 0), 0,
                (_, subject, path) =>
                {
                    attempted++;
                    Assert.Contains("1.2.48", subject);
                    Assert.Contains(sql, File.ReadAllText(path));
                    return status;
                });
            Assert.Equal(1, attempted);
            var report = File.ReadAllText(result.ReportPath);
            Assert.Contains("PNCP King 1.2.48.0", report);
            Assert.DoesNotContain("unrelated", report);
            if (status is 0 or 1)
            {
                Assert.Equal(status == 0 ? DiagnosticMail.Outcome.Submitted : DiagnosticMail.Outcome.Canceled, result.Status);
                Assert.Null(result.DraftPath);
                Assert.Empty(directory.GetFiles("*.eml"));
            }
            else
            {
                Assert.Equal(DiagnosticMail.Outcome.Draft, result.Status);
                var mime = File.ReadAllText(result.DraftPath!);
                Assert.StartsWith("X-Unsent: 1\r\nTo: felipearcencio@gmail.com\r\n", mime);
                var part = mime.Split("Content-Disposition: attachment;")[1];
                Assert.Contains(Path.GetFileName(result.ReportPath), part);
                var payload = part.Split("\r\n\r\n")[1].Split("\r\n--")[0];
                Assert.Equal(File.ReadAllBytes(result.ReportPath), Convert.FromBase64String(payload));
            }
        }
        finally { directory.Delete(true); }
    }
}
