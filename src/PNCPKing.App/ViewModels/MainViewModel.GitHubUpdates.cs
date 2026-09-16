using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using PNCPKing.App.Services;
using PNCPKing.App.Views;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    public ICommand GitHubUpdateCommand { get; private set; } = null!;
    private Task? _gitHubUpdateTask;
    private static Version ApplicationVersion => new(typeof(MainViewModel).Assembly.GetName().Version!.ToString(3));

    private async Task UpdateFromGitHubAsync()
    {
        var restart = false;
        await RunFileOperationAsync(async ct =>
        {
            try
            {
                _maintenanceCoordinator.NotifyVisibleActivity();
                IsFileOperationIndeterminate = true;
                FileOperationProgressText = "Consultando publicações no GitHub…";
                using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
                    { Timeout = Timeout.InfiniteTimeSpan };
                var downloads = new GitHubUpdateService(http);
                var official = new OfficialUpdateService(_calibrationService.Connections);
                var check = await downloads.CheckAsync(ct);
                var state = await official.GetTransferStatusAsync(ct);
                var plan = GitHubUpdateValidation.Plan(check, ApplicationVersion, SqliteContractRepository.CurrentSchemaVersion, state);
                ct.ThrowIfCancellationRequested();
                var preview = new GitHubUpdateWindow(plan) { Owner = Application.Current.MainWindow };
                if (preview.ShowDialog() != true) return;
                ct.ThrowIfCancellationRequested();
                if (plan.App is not null) GitHubAppInstaller.CheckDestination();
                var databasePath = _calibrationService.Connections.DatabasePath;
                if (plan.Packages.Count > 0)
                    GitHubUpdateService.EnsureSpace(Path.GetDirectoryName(databasePath)!, plan.Packages.Max(p => p.ExpandedSize));
                Directory.CreateDirectory(GitHubAppInstaller.CacheDirectory);
                GitHubUpdateService.EnsureSpace(GitHubAppInstaller.CacheDirectory, checked(plan.DownloadSize * 2));
                var downloadedPrices = new List<DownloadedPriceUpdate>();
                var progress = new Progress<UpdateDownloadProgress>(p =>
                {
                    FileOperationProgressText = p.Message;
                    IsFileOperationIndeterminate = p.Message.StartsWith("Recompondo", StringComparison.Ordinal);
                    OperationProgress = p.Total > 0 ? 100d * p.Received / p.Total : 0;
                });
                string? executable = null;
                if (plan.App is { } app)
                {
                    executable = await downloads.DownloadAsync(check.App!.Tag,
                        new(app.File.Size, app.File.Sha256, [app.File]), GitHubAppInstaller.CacheDirectory, progress, ct);
                    FileOperationProgressText = "Validando a versão do programa…";
                    await GitHubAppInstaller.ValidateBinaryAsync(executable, app, ct);
                }
                foreach (var package in plan.Packages)
                {
                    var path = await downloads.DownloadAsync(check.Prices!.Tag, package.Download, GitHubAppInstaller.CacheDirectory, progress, ct);
                    FileOperationProgressText = "Conferindo o manifesto do pacote de preços…";
                    await GitHubUpdateService.ValidatePackageAsync(path, package, ct);
                    downloadedPrices.Add(new(path, package));
                }
                var pending = new PendingGitHubUpdate(databasePath, state.Origin, state.BaseId,
                    plan.App?.Version ?? ApplicationVersion.ToString(3), plan.ReplaceBase, downloadedPrices);
                if (executable is not null)
                {
                    FileOperationProgressText = "Preparando instalação e reinício do programa…";
                    await GitHubAppInstaller.PrepareAsync(executable, plan.App!, pending, ct);
                    // Handoff is now committed; close through the normal window shutdown path.
                    CanCancelFileOperation = false;
                    restart = true;
                }
                else await ApplyGitHubPricesAsync(pending, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "Atualização interrompida. Partes verificadas e lotes importados foram preservados; clique novamente para continuar.";
            }
        });
        if (restart && !_disposed) Application.Current.MainWindow.Close();
    }

    public Task ContinueGitHubUpdateAsync(string id)
    {
        _gitHubUpdateTask = RunFileOperationAsync(async ct =>
        {
            try
            {
                var pending = await GitHubAppInstaller.ReadPendingAsync(id, ct);
                if (ApplicationVersion != GitHubUpdateValidation.ParseVersion(pending.AppVersion))
                    throw new InvalidDataException("A versão instalada não corresponde à atualização pendente.");
                await ApplyGitHubPricesAsync(pending, ct);
                GitHubAppInstaller.Complete(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "Programa atualizado. Importação interrompida; lotes concluídos preservados. Use Atualizar pelo GitHub para continuar.";
            }
            catch (Exception e) when (!AsyncCommandRuntime.IsCritical(e))
            {
                throw new IOException("O programa foi atualizado, mas a etapa dos preços não foi concluída. " +
                    "Use Atualizar pelo GitHub para tentar novamente. " + e.Message, e);
            }
        });
        return _gitHubUpdateTask;
    }

    private async Task ApplyGitHubPricesAsync(PendingGitHubUpdate pending, CancellationToken ct)
    {
        var official = new OfficialUpdateService(_calibrationService.Connections);
        var state = await official.GetTransferStatusAsync(ct);
        if (!string.Equals(Path.GetFullPath(pending.DatabasePath), Path.GetFullPath(_calibrationService.Connections.DatabasePath),
                StringComparison.OrdinalIgnoreCase) || state.Origin != pending.DatabaseOrigin || state.BaseId != pending.PreviousBaseId)
            throw new InvalidDataException("O banco selecionado ou sua base mudou. Consulte novamente as atualizações antes de importar.");
        // Validate every local payload before importing the first one. Resume never initiates another network request.
        foreach (var item in pending.Packages)
        {
            var expectedPath = Path.Combine(GitHubAppInstaller.CacheDirectory, item.Package.Download.Sha256.ToLowerInvariant() + ".payload");
            if (!string.Equals(Path.GetFullPath(item.Path), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                item.Package.Manifest.Schema != SqliteContractRepository.CurrentSchemaVersion)
                throw new InvalidDataException("Pacote pendente incompatível.");
            await GitHubUpdateService.ValidatePackageAsync(item.Path, item.Package, ct);
        }
        if (pending.Packages.Count == 0) { StatusText = "Programa atualizado com sucesso."; return; }
        IsFileOperationIndeterminate = true;
        var progress = new Progress<string>(s => FileOperationProgressText = s);
        long conflicts = 0;
        try
        {
            foreach (var item in pending.Packages)
            {
                var result = await Task.Run(() => official.ImportAsync(item.Path, progress, ct,
                    pending.ReplaceBase && item.Package.Manifest.InitialBase), ct);
                conflicts += result.Conflicts;
            }
            StatusText = $"Atualização concluída. Preços importados; pendências de revalidação: {conflicts:N0}.";
            if (conflicts > 0)
                MessageBox.Show("As versões locais foram preservadas nas divergências sem ordem oficial confiável. " +
                    "Use Atualizar para revalidar os dados PNCP e Atualizar catálogo para CATMAT/CATSER.",
                    "Revalidação pendente", MessageBoxButton.OK, MessageBoxImage.Information);
            foreach (var item in pending.Packages) File.Delete(item.Path);
        }
        finally { if (!_disposed) await RefreshAfterOfficialImportAsync(); }
    }
}
