using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    public ICommand ExportUpdatesCommand { get; private set; } = null!;
    public ICommand ImportUpdatesCommand { get; private set; } = null!;
    public ICommand CompactDatabaseCommand { get; private set; } = null!;

    private void InitializeUpdates()
    {
        GitHubUpdateCommand = new AsyncRelayCommand(() => _gitHubUpdateTask = UpdateFromGitHubAsync(), () => CanEvaluatePc);
        ExportUpdatesCommand = new AsyncRelayCommand(ExportOfficialUpdatesAsync, () => CanEvaluatePc);
        ImportUpdatesCommand = new AsyncRelayCommand(ImportOfficialUpdatesAsync, () => CanEvaluatePc);
        CompactDatabaseCommand = new AsyncRelayCommand(async () =>
        {
            await RunFileOperationAsync(async ct =>
            {
                // VACUUM can renumber rowids, including after retention has committed.
                InvalidateLocalPriceCursor();
                var repository = new SqliteContractRepository(_calibrationService.Connections);
                var progress = new Progress<string>(s => FileOperationProgressText = s);
                var result = await Task.Run(() => repository.MaintainRetentionAsync(DateOnly.FromDateTime(DateTime.Today),
                    compact: true, force: true, cancellationToken: ct, progress: progress), ct);
                StatusText = result.Message;
            });
        }, () => CanEvaluatePc);
    }

    private async Task ExportOfficialUpdatesAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar atualizações PNCP",
            Filter = "Atualizações PNCP (*.pncpupdate)|*.pncpupdate",
            DefaultExt = ".pncpupdate",
            FileName = $"PNCP-atualizacoes-{DateTime.Today:yyyyMMdd}.pncpupdate"
        };
        if (dialog.ShowDialog() != true) return;
        await RunFileOperationAsync(async ct =>
        {
            _maintenanceCoordinator.NotifyVisibleActivity();
            IsFileOperationIndeterminate = true;
            var progress = new Progress<string>(s => FileOperationProgressText = s);
            var service = new OfficialUpdateService(_calibrationService.Connections);
            var result = await Task.Run(() => service.ExportAsync(dialog.FileName, progress, ct), ct);
            StatusText = $"Atualização v2 exportada: {result.StartDate:dd/MM/yyyy} a {result.EndDate:dd/MM/yyyy}. Pode ser aplicada diretamente a um backup.";
        });
    }

    private async Task ImportOfficialUpdatesAsync()
    {
        var dialog = new OpenFileDialog { Title = "Importar atualizações PNCP", Filter = "Atualizações PNCP (*.pncpupdate)|*.pncpupdate" };
        if (dialog.ShowDialog() != true) return;
        await RunFileOperationAsync(async ct =>
        {
            _maintenanceCoordinator.NotifyVisibleActivity();
            IsFileOperationIndeterminate = true;
            var progress = new Progress<string>(s => FileOperationProgressText = s);
            var service = new OfficialUpdateService(_calibrationService.Connections);
            try
            {
                var result = await Task.Run(() => service.ImportAsync(dialog.FileName, progress, ct), ct);
                StatusText = $"Atualizações importadas: {result.Applied:N0}; versões locais preservadas: {result.Skipped:N0}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "Importação interrompida; lotes concluídos preservados. Reimporte o pacote para continuar.";
            }
            finally
            {
                await RefreshAfterOfficialImportAsync();
            }
        });
    }

    private async Task RefreshAfterOfficialImportAsync()
    {
        _calibrationService.Connections.ResetCalibration();
        _calibrationWindow?.Close();
        await SaveCalibrationAsync(null);
        await _itemSearchService.InvalidateAsync();
        await _transientItemSearchService.InvalidateAsync();
        await RefreshDatasetSummaryAsync();
        await RefreshCoverageAsync();
        await RefreshPriceCacheProgressAsync();
        await RefreshNationalPriceIndexProgressAsync();
        await RefreshCatalogCoverageAsync();
        _restartPriceSessionOnNextExpansion = true;
        var message = StatusText;
        await SearchAsync(resetSession: true, restartPriceSession: true, revalidateStalePrices: false);
        StatusText = message;
    }
}
