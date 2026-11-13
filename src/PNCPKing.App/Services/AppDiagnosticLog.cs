using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Services;

public sealed class AppDiagnosticLog
{
    private readonly object _gate = new();
    private readonly ISystemResourceProbe _resourceProbe;
    private string? _lastError;

    public string? LastError => Volatile.Read(ref _lastError);

    public AppDiagnosticLog(ISystemResourceProbe? resourceProbe = null)
        : this(resourceProbe, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PNCP King", "logs")) { }

    internal AppDiagnosticLog(ISystemResourceProbe? resourceProbe, string directoryPath)
    {
        _resourceProbe = resourceProbe ?? new SystemResourceProbe();
        DirectoryPath = directoryPath;
        Directory.CreateDirectory(DirectoryPath);
        CleanupOldLogs();
        FilePath = Path.Combine(
            DirectoryPath,
            $"pncpking-{DateTime.Now:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.log");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public void WriteStartupHeader()
    {
        using var process = Process.GetCurrentProcess();
        var resources = _resourceProbe.GetSnapshot();
        Info(
            "startup",
            $"PNCP King iniciado. Versão={typeof(AppDiagnosticLog).Assembly.GetName().Version}; " +
            $"SO={RuntimeInformation.OSDescription}; arquitetura={RuntimeInformation.OSArchitecture}; " +
            $"processo={process.Id}; ram_fisica_total={resources.TotalPhysicalMemoryBytes}; " +
            $"ram_fisica_livre={resources.AvailablePhysicalMemoryBytes}; carga_memoria={resources.MemoryLoadPercent}; " +
            $"limite_memoria_GC={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}; " +
            $"memoria_privada_processo={process.PrivateMemorySize64}; " +
            $"pasta_execução={AppContext.BaseDirectory}");
    }

    public void Info(string area, string message) => Write("INFO", area, message, exception: null);

    public void Warning(string area, string message) => Write("WARN", area, message, exception: null);

    public string Error(string area, string message, Exception exception) =>
        Write("ERROR", area, message, exception);

    private string Write(string level, string area, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] [")
            .Append(area.Trim()).Append("] ")
            .AppendLine(message.Trim());
        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
            for (var cause = exception; cause is not null; cause = cause.InnerException)
            {
                if (cause.Data[BackupService.TransferSchemaDiagnosticKey] is string definition)
                    builder.AppendLine(definition);
                if (cause.Data[QuotationEvidenceExportService.DiagnosticKey] is string evidence)
                    builder.AppendLine(evidence);
            }
        }

        var entry = builder.ToString();
        lock (_gate)
        {
            if (exception is not null) Volatile.Write(ref _lastError, entry);
            try { File.AppendAllText(FilePath, entry, Encoding.UTF8); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A logging failure must not hide the incident or prevent shutdown.
            }
        }
        return entry;
    }

    private void CleanupOldLogs()
    {
        var threshold = DateTime.UtcNow.AddDays(-30);
        var files = Directory.EnumerateFiles(DirectoryPath, "pncpking-*.log")
            .Concat(Directory.EnumerateFiles(DirectoryPath, "pncpking-erro-*.txt"))
            .Concat(Directory.EnumerateFiles(DirectoryPath, "pncpking-erro-*.eml"));
        foreach (var path in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Um log ainda aberto por outra instância é preservado.
            }
        }
    }
}
