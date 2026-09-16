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
    public ICommand NewUpdateBaseCommand { get; private set; } = null!;
    public ICommand ImportNewUpdateBaseCommand { get; private set; } = null!;
    public ICommand CompactDatabaseCommand { get; private set; } = null!;

    private void InitializeUpdates()
    {
        GitHubUpdateCommand = new AsyncRelayCommand(() => _gitHubUpdateTask = UpdateFromGitHubAsync(), () => CanEvaluatePc);
        ExportUpdatesCommand = new AsyncRelayCommand(() => ExportOfficialUpdatesAsync(false), () => CanEvaluatePc);
        NewUpdateBaseCommand = new AsyncRelayCommand(() => ExportOfficialUpdatesAsync(true), () => CanEvaluatePc);
        ImportUpdatesCommand = new AsyncRelayCommand(() => ImportOfficialUpdatesAsync(false), () => CanEvaluatePc);
        ImportNewUpdateBaseCommand = new AsyncRelayCommand(() => ImportOfficialUpdatesAsync(true), () => CanEvaluatePc);
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

    private async Task ExportOfficialUpdatesAsync(bool newBase)
    {
        if (newBase && MessageBox.Show("Criar uma nova base oficial completa? Os outros PCs precisarão iniciar uma nova linhagem com essa base. Os pacotes da base anterior continuarão incompatíveis com a nova.",
            "Nova base de transferência", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
            var result = await Task.Run(() => service.ExportAsync(dialog.FileName, newBase, progress, ct), ct);
            StatusText = result.InitialBase ? "Base oficial inicial exportada. Importe este arquivo uma vez no outro PC." : "Atualizações cumulativas exportadas. Basta transportar este pacote mais recente.";
        });
    }

    private async Task ImportOfficialUpdatesAsync(bool replaceBase)
    {
        if (replaceBase && MessageBox.Show("Trocar a linhagem pela base inicial escolhida? Os dados particulares e as versões oficiais mais novas serão preservados. Os pacotes anteriores deixarão de ser compatíveis.",
            "Importar nova base", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
                var result = await Task.Run(() => service.ImportAsync(dialog.FileName, progress, ct, replaceBase), ct);
                StatusText = $"Atualizações importadas: {result.Applied:N0}; preservadas: {result.Skipped:N0}; pendências de revalidação: {result.Conflicts:N0}.";
                if (result.Conflicts > 0)
                    MessageBox.Show("As versões do destino foram preservadas onde não havia uma ordem oficial confiável. Use Atualizar para revalidar os dados PNCP; divergências de CATMAT/CATSER são revalidadas em Atualizar catálogo.",
                        "Revalidação pendente", MessageBoxButton.OK, MessageBoxImage.Information);
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
