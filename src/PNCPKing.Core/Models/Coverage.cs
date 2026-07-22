namespace PNCPKing.Core.Models;

public enum CoverageStatus
{
    Missing = 0,
    Partial = 1,
    Downloading = 2,
    Complete = 3,
    AssumedComplete = 4,
    Failed = 5
}

public sealed record CoverageDay
{
    public required DateOnly Date { get; init; }
    public required CoverageStatus Status { get; init; }
    public required int ExpectedModalities { get; init; }
    public required int CompletedModalities { get; init; }
    public long? RecordsCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? LastError { get; init; }

    public bool IsComplete => Status is CoverageStatus.Complete or CoverageStatus.AssumedComplete;

    public double Percentage => ExpectedModalities == 0
        ? 0d
        : Math.Clamp(CompletedModalities * 100d / ExpectedModalities, 0d, 100d);

    public string ToolTip => $"{Date:dd/MM/yyyy} - {StatusLabel}\n" +
        $"Modalidades: {CompletedModalities:N0} de {ExpectedModalities:N0}" +
        (RecordsCount is null ? string.Empty : $"\nRegistros: {RecordsCount:N0}") +
        (string.IsNullOrWhiteSpace(LastError) ? string.Empty : $"\nErro: {LastError}");

    public string StatusLabel => Status switch
    {
        CoverageStatus.Missing => "Ausente",
        CoverageStatus.Partial => "Parcial",
        CoverageStatus.Downloading => "Baixando",
        CoverageStatus.Complete => "Completo",
        CoverageStatus.AssumedComplete => "Completo (base anterior)",
        CoverageStatus.Failed => "Falha",
        _ => "Desconhecido"
    };
}

public sealed record CoverageSummary(
    IReadOnlyList<CoverageDay> Days,
    int CompleteDays,
    int TotalDays)
{
    public double Percentage => TotalDays == 0 ? 0d : CompleteDays * 100d / TotalDays;

    public string Display => $"{CompleteDays:N0} de {TotalDays:N0} dias completos - {Percentage:N1}%";
}

public sealed record CoverageWorkItem(DateOnly Date, long ModalityId, string Uf);

public sealed record AutoSyncResult(
    DateOnly StartDate,
    DateOnly EndDate,
    int CoverageBatches,
    bool GlobalUpdateCompleted);
