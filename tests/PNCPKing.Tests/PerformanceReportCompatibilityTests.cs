using System.Text.Json;
using PNCPKing.Core.Models;

namespace PNCPKing.Tests;

public sealed class PerformanceReportCompatibilityTests
{
    [Fact]
    public void LegacyJson_DeserializesWithNewTelemetryFieldsAtDefaults()
    {
        const string json = """
            {
              "GeneratedAt": "2026-08-12T14:25:00-03:00",
              "ApplicationVersion": "1.0.0.0",
              "OperatingSystem": "Microsoft Windows 10",
              "Framework": ".NET 8.0",
              "LogicalProcessors": 4,
              "AvailableMemoryBytes": 805306368,
              "DatabaseBytes": 2000000000,
              "WalBytes": 1000000000,
              "Measurements": [],
              "Summaries": [],
              "BaselineApplicationVersion": "",
              "Comparisons": []
            }
            """;

        var report = JsonSerializer.Deserialize<PerformanceReport>(json);

        Assert.NotNull(report);
        Assert.Equal(805306368, report.AvailableMemoryBytes);
        Assert.Equal(0, report.TotalPhysicalMemoryBytes);
        Assert.Equal(0, report.FreePhysicalMemoryBytes);
        Assert.Equal(string.Empty, report.BuildIdentifier);
        Assert.Empty(report.ActiveOperations);
    }
}
