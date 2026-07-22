using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public sealed class PreflightService(IPncpClient client)
{
    public async Task<PreflightEstimate> CalculateAsync(
        DateOnly startDate,
        DateOnly endDate,
        GeoScope scope,
        string dataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modalities = await client.GetModalitiesAsync(cancellationToken).ConfigureAwait(false);
        long totalContracts = 0;
        long estimatedTransferBytes = 0;
        long estimatedRequests = 0;
        var measuredDurations = new List<TimeSpan>();
        var completed = 0;
        var totalQueries = modalities.Count * scope.ApiUfFilters.Count;

        foreach (var uf in scope.ApiUfFilters)
        {
            foreach (var modality in modalities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Contando {modality.Name} — {uf ?? "Brasil"} ({++completed}/{totalQueries})");
                var page = await client.GetContractsPageAsync(
                    startDate,
                    endDate,
                    modality.Id,
                    uf,
                    1,
                    10,
                    SyncMode.Publication,
                    cancellationToken).ConfigureAwait(false);

                totalContracts += page.TotalRecords;
                var sampleBytes = page.Contracts.Count == 0
                    ? 1_800d
                    : Math.Max(500d, page.PayloadBytes / (double)page.Contracts.Count);
                estimatedTransferBytes += checked((long)Math.Ceiling(page.TotalRecords * sampleBytes));
                estimatedRequests += (long)Math.Ceiling(page.TotalRecords / 50d);
                measuredDurations.Add(page.Elapsed);
            }
        }

        if (estimatedTransferBytes == 0 && totalContracts > 0)
        {
            estimatedTransferBytes = checked(totalContracts * 1_800);
        }

        var databaseMin = checked((long)Math.Ceiling(estimatedTransferBytes * 1.2));
        var databaseMax = checked((long)Math.Ceiling(estimatedTransferBytes * 2.4));
        var fullCacheMin = checked(totalContracts * 14_000);
        var fullCacheMax = checked(totalContracts * 28_000);
        var requiredFree = checked((long)Math.Ceiling(databaseMax * 1.2));
        var available = GetAvailableFreeSpace(dataPath);
        var averageRequest = measuredDurations.Count == 0
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromMilliseconds(Math.Max(250, measuredDurations.Average(item => item.TotalMilliseconds)));

        return new PreflightEstimate
        {
            StartDate = startDate,
            EndDate = endDate,
            Scope = scope,
            ExactContractCount = totalContracts,
            EstimatedTransferBytes = estimatedTransferBytes,
            EstimatedDatabaseMinBytes = databaseMin,
            EstimatedDatabaseMaxBytes = databaseMax,
            EstimatedFullCacheMinBytes = fullCacheMin,
            EstimatedFullCacheMaxBytes = fullCacheMax,
            RequiredFreeBytes = requiredFree,
            AvailableFreeBytes = available,
            EstimatedRequests = estimatedRequests,
            EstimatedDuration = TimeSpan.FromMilliseconds(averageRequest.TotalMilliseconds * estimatedRequests)
        };
    }

    private static long GetAvailableFreeSpace(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return 0;
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}
