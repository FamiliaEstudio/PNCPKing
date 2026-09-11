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
        var progress = new Progress<string>(message =>
        {
            if (compact) SetStartupPhase(message);
            else FileOperationProgressText = message;
        });
        var result = await Task.Run(() => repository.MaintainRetentionAsync(
            today, compact, cancellationToken: cancellationToken, progress: progress), cancellationToken).ConfigureAwait(true);
        if (compact && result.Applied)
        {
            await _itemSearchService.InvalidateAsync(cancellationToken).ConfigureAwait(true);
            await _transientItemSearchService.InvalidateAsync(cancellationToken).ConfigureAwait(true);
        }
        var quotationRepository = new SqliteQuotationRepository(repository.DatabasePath);
        var hashes = await quotationRepository.GetReferencedInternetEvidenceHashesAsync(cancellationToken).ConfigureAwait(true);
        await _internetEvidenceStore.DeleteOrphansAsync(hashes, cancellationToken).ConfigureAwait(true);
        _lastRetentionDate = today;
        _diagnosticLog.Info("retention", result.Message);
        MaintenanceActivityText = result.Message;
        // A large first compaction can cross midnight while startup is blocked.
        if (DateOnly.FromDateTime(DateTime.Today) != today)
            await ApplyLocalRetentionAsync(compact: false, cancellationToken).ConfigureAwait(true);
    }

    private async Task ApplyDailyRetentionAsync()
    {
        if (_disposed || IsInitializing || _lastRetentionDate is null || _retentionRunning ||
            DateOnly.FromDateTime(DateTime.Today) == _lastRetentionDate || DateTimeOffset.UtcNow < _nextRetentionAttempt)
            return;
        if (IsFileBusy)
        {
            _quotationAutomationCancellation?.Cancel();
            return;
        }
        // Let an already open modal dialog finish before taking its owner's UI.
        if (Application.Current.Windows.Cast<Window>().Any(window => window.IsVisible && !window.IsEnabled))
            return;
        _retentionRunning = true;
        IsFileBusy = true;
        IsFileOperationIndeterminate = true;
        FileOperationProgressText = "Atualizando a janela de 11 meses…";
        var windows = Application.Current.Windows.Cast<Window>()
            .Select(window => (Window: window, Enabled: window.IsEnabled)).ToArray();
        foreach (var entry in windows) entry.Window.IsEnabled = false;
        NotifyCommands();
        try
        {
            _maintenanceCoordinator.NotifyVisibleActivity();
            _indexCancellation?.Cancel();
            _catalogCancellation?.Cancel();
            _contractSearchCancellation?.Cancel();
            _contractCountCancellation?.Cancel();
            _selectedContractCacheCancellation?.Cancel();
            _priceCancellation?.Cancel();
            _foregroundCancellation?.Cancel();
            _documentCancellation?.Cancel();
            _quotationAutomationCancellation?.Cancel();
            if (_quotationAutomationCompletion is { } completion)
                await completion.Task.ConfigureAwait(true);
            await StopAggressivePriceCacheAsync(scheduleNormalMaintenance: false).ConfigureAwait(true);
            await StopAggressiveNationalPriceIndexAsync(scheduleNormalMaintenance: false).ConfigureAwait(true);
            await CancelAndAwaitPriceCacheCycleAsync().ConfigureAwait(true);
            await CancelAndAwaitNationalPriceIndexCycleAsync().ConfigureAwait(true);
            if (_quotationItemWindow is { } itemWindow)
                await itemWindow.PrepareForRetentionAsync().ConfigureAwait(true);
            await _itemSearchService.InvalidateAsync(_startupCancellation.Token).ConfigureAwait(true);
            await _transientItemSearchService.InvalidateAsync(_startupCancellation.Token).ConfigureAwait(true);
            while (IsIndexBusy || IsCatalogBusy || IsPriceBusy || IsForegroundBusy || IsDocumentBusy || _automaticMaintenanceRunning)
                await Task.Delay(50, _startupCancellation.Token).ConfigureAwait(true);
            Interlocked.Increment(ref _priceRunGeneration);
            _retainedItemRows.Clear();
            ItemSearchRows.Clear();
            ContractItemRows.Clear();
            _localPriceCursor = null;
            _restartPriceSessionOnNextExpansion = true;
            await ApplyLocalRetentionAsync(compact: false, _startupCancellation.Token).ConfigureAwait(true);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var range = DataWindow.Normalize(
                DateOnly.FromDateTime(CustomStartDate ?? DateTime.Today),
                DateOnly.FromDateTime(CustomEndDate ?? DateTime.Today), today);
            CustomStartDate = range.Start.ToDateTime(TimeOnly.MinValue);
            CustomEndDate = range.End.ToDateTime(TimeOnly.MinValue);
            await SearchAsync(resetSession: true, restartPriceSession: true, revalidateStalePrices: false).ConfigureAwait(true);
            await RefreshDatasetSummaryAsync().ConfigureAwait(true);
            await RefreshCoverageAsync().ConfigureAwait(true);
            await RefreshPriceCacheProgressAsync().ConfigureAwait(true);
            await RefreshNationalPriceIndexProgressAsync().ConfigureAwait(true);
            if (_quotationsInitialized)
            {
                await RefreshQuotationProjectsAsync().ConfigureAwait(true);
                await LoadQuotationProjectAsync(SelectedQuotationProject?.Id).ConfigureAwait(true);
            }
            if (_quotationItemWindow is { } refreshedWindow)
                await refreshedWindow.ViewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            _nextRetentionAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
            _diagnosticLog.Error("retention", "Não foi possível concluir a manutenção da janela de 11 meses.", exception);
            MaintenanceActivityText = $"Limpeza pendente: {exception.Message}";
        }
        finally
        {
            foreach (var entry in windows) entry.Window.IsEnabled = entry.Enabled;
            IsFileOperationIndeterminate = false;
            IsFileBusy = false;
            _retentionRunning = false;
            NotifyCommands();
        }
    }
}
