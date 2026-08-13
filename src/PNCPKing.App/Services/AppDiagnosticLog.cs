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

    public AppDiagnosticLog(ISystemResourceProbe? resourceProbe = null)
    {
        _resourceProbe = resourceProbe ?? new SystemResourceProbe();
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DirectoryPath = Path.Combine(applicationData, "PNCP King", "logs");
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

    public void Error(string area, string message, Exception exception) =>
        Write("ERROR", area, message, exception);

    private void Write(string level, string area, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [").Append(level).Append("] [")
                .Append(area.Trim()).Append("] ")
                .AppendLine(message.Trim());
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (_gate)
            {
                File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception logException) when (
            logException is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // O diagnóstico nunca deve impedir a abertura ou o encerramento do aplicativo.
        }
    }

    private void CleanupOldLogs()
    {
        var threshold = DateTime.UtcNow.AddDays(-30);
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "pncpking-*.log"))
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
