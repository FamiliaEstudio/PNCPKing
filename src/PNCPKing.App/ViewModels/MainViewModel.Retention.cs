using System.Windows;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private DateOnly? _lastRetentionDate;
    private bool _retentionRunning;
    private DateTimeOffset _nextRetentionAttempt;

    private async Task ApplyLocalRetentionAsync(bool compact, CancellationToken cancellationToken)
    {
        if (_repository is not SqliteContractRepository repository)
            return;

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (compact)
        {
            var progress = new Progress<string>(message =>
            {
                if (IsInitializing) SetStartupPhase(message);
                else FileOperationProgressText = message;
            });
            var result = await Task.Run(() => repository.MaintainRetentionAsync(
                today, compact: true, cancellationToken: cancellationToken, progress: progress), cancellationToken)
                .ConfigureAwait(true);
            _lastRetentionDate = result.Pending ? null : today;
            _diagnosticLog.Info("retention", result.Message);
            MaintenanceActivityText = result.Message;
            return;
        }

        // Startup only advances the logical 11-month window. Physical deletion is
        // intentionally deferred so old mechanical disks never pay the cleanup cost
        // before the UI becomes usable.
        _lastRetentionDate = await repository.PrepareRetentionWindowAsync(today, cancellationToken).ConfigureAwait(true);
        MaintenanceActivityText = _lastRetentionDate == today
            ? "Janela de 11 meses atualizada."
            : "Janela de 11 meses atualizada; limpeza física aguardando ociosidade.";
    }

    private async Task ApplyDailyRetentionAsync()
    {
        if (_disposed || IsInitializing || _retentionRunning ||
            DateOnly.FromDateTime(DateTime.Today) == _lastRetentionDate ||
            DateTimeOffset.UtcNow < _nextRetentionAttempt ||
            IsFileBusy || IsIndexBusy || IsCatalogBusy || IsPriceBusy || IsForegroundBusy || IsDocumentBusy ||
            _automaticMaintenanceRunning)
            return;

        var decision = _maintenanceCoordinator.GetDecision();
        if (!decision.CanRun)
        {
            MaintenanceActivityText = $"Limpeza da janela aguardando: {decision.Description}.";
            return;
        }

        await using var maintenanceLease = _maintenanceCoordinator.TryEnter();
        if (maintenanceLease is null)
            return;

        if (_repository is not SqliteContractRepository repository)
            return;

        _retentionRunning = true;
        try
        {
            // Keep each physical delete deliberately small. The next idle tick resumes
            // from the remaining expired rows, so a 15-year-old HDD never receives one
            // giant startup transaction.
            var batchSize = decision.Resources.Pressure == SystemResourcePressure.Constrained ? 25 : 75;
            var today = DateOnly.FromDateTime(DateTime.Today);
            var result = await repository.MaintainRetentionAsync(
                today,
                compact: false,
                cancellationToken: _startupCancellation.Token,
                contractBatchSize: batchSize).ConfigureAwait(true);

            if (result.Applied)
            {
                await _itemSearchService.InvalidateAsync(_startupCancellation.Token).ConfigureAwait(true);
                await _transientItemSearchService.InvalidateAsync(_startupCancellation.Token).ConfigureAwait(true);
            }

            _diagnosticLog.Info("retention", result.Message);
            MaintenanceActivityText = result.Message;

            if (result.Pending)
            {
                // Yield to visible work and continue only after another idle interval.
                _nextRetentionAttempt = DateTimeOffset.UtcNow.Add(decision.RetryDelay);
            }
            else
            {
                _lastRetentionDate = today;
                _nextRetentionAttempt = DateTimeOffset.MinValue;
                var quotationRepository = new SqliteQuotationRepository(repository.DatabasePath);
                var hashes = await quotationRepository.GetReferencedInternetEvidenceHashesAsync(_startupCancellation.Token)
                    .ConfigureAwait(true);
                await _internetEvidenceStore.DeleteOrphansAsync(hashes, _startupCancellation.Token).ConfigureAwait(true);
                await RefreshDatasetSummaryAsync().ConfigureAwait(true);
                await RefreshCoverageAsync().ConfigureAwait(true);
                await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
                await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_disposed || _startupCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _nextRetentionAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
            _diagnosticLog.Error("retention", "Não foi possível concluir um lote da manutenção da janela de 11 meses.", exception);
            MaintenanceActivityText = $"Limpeza pendente: {exception.Message}";
        }
        finally
        {
            _retentionRunning = false;
        }
    }

}
