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
        string? downloadedPrices = null;
        await RunFileOperationAsync(async ct =>
        {
            _maintenanceCoordinator.NotifyVisibleActivity();
            IsFileOperationIndeterminate = true;
            FileOperationProgressText = "Consultando o programa no GitHub e a base de preços no Google Drive…";
            using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
                { Timeout = Timeout.InfiniteTimeSpan };
            var downloads = new GitHubUpdateService(http);
            var check = await downloads.CheckProgramAsync(ct);
            var plan = GitHubUpdateValidation.Plan(check, ApplicationVersion, SqliteContractRepository.CurrentSchemaVersion,
                new OfficialTransferStatus(new Dictionary<string, OfficialImportReceipt>()));
            IReadOnlyList<DrivePriceFile> files = [];
            using var driveHttp = DrivePriceUpdateService.CreateClient();
            var drive = new DrivePriceUpdateService(driveHttp);
            try
            {
                files = await drive.ListAsync(ct);
                plan = plan with { PricesStatus = files.Count == 0
                    ? "Nenhum arquivo .pncpking disponível na pasta do Google Drive."
                    : $"Google Drive: {files.Count} pacote(s) de base completa disponível(is)." };
            }
            catch (Exception e) when (!AsyncCommandRuntime.IsCritical(e) && !ct.IsCancellationRequested)
            {
                // An unavailable price source must not prevent a program-only update.
                plan = plan with { PricesStatus = "Preços indisponíveis: " + e.Message };
            }
            ct.ThrowIfCancellationRequested();
            var notes = plan.App is null ? new GitHubReleaseNotes([], null) :
                await downloads.GetReleaseNotesAsync(ApplicationVersion,
                    GitHubUpdateValidation.ParseVersion(plan.App.Version), ct);
            ct.ThrowIfCancellationRequested();
            var preview = new GitHubUpdateWindow(plan, notes, driveFiles: files)
                { Owner = Application.Current.MainWindow };
            if (preview.ShowDialog() != true) return;
            ct.ThrowIfCancellationRequested();
            var app = preview.UpdateProgram ? plan.App : null;
            var prices = preview.UpdatePrices ? preview.SelectedDriveFile : null;
            if (app is null && prices is null) return;
            if (prices is not null && (_quotationItemWindow is not null || _calibrationWindow is not null))
                throw new InvalidOperationException("Feche as janelas de item da cotação e de avaliação do PC antes de atualizar os preços.");
            if (app is not null)
            {
                GitHubAppInstaller.CheckDestination();
                Directory.CreateDirectory(GitHubAppInstaller.CacheDirectory);
                GitHubUpdateService.EnsureSpace(GitHubAppInstaller.CacheDirectory, checked(app.File.Size * 2));
                var executable = await downloads.DownloadAsync(check.App!.Tag,
                    new(app.File.Size, app.File.Sha256, [app.File]), GitHubAppInstaller.CacheDirectory,
                    CreateDownloadProgress(), ct);
                FileOperationProgressText = "Validando a versão do programa…";
                await GitHubAppInstaller.ValidateBinaryAsync(executable, app, ct);
                var pending = new PendingGitHubUpdate(_calibrationService.Connections.DatabasePath, app.Version,
                    null, UpdatePricesAfterRestart: prices is not null, DrivePrices: prices);
                FileOperationProgressText = "Preparando instalação e reinício do programa…";
                await GitHubAppInstaller.PrepareAsync(executable, app, pending, ct);
                CanCancelFileOperation = false;
                restart = true;
            }
            else if (prices is not null)
                downloadedPrices = await DownloadDrivePricesAsync(prices, ct);
        });
        if (restart && !_disposed) Application.Current.MainWindow.Close();
        // Leave the download operation before entering the unchanged backup import workflow.
        // It owns its normal confirmation, cancellation, background pauses and preservation.
        else if (downloadedPrices is not null && !_disposed)
            await ImportBackupAsync(pricesOnly: true, sourcePath: downloadedPrices);
    }

    private IProgress<UpdateDownloadProgress> CreateDownloadProgress() => new Progress<UpdateDownloadProgress>(p =>
    {
        FileOperationProgressText = p.Message;
        IsFileOperationIndeterminate = p.Total <= 0;
        OperationProgress = p.Total > 0 ? 100d * p.Received / p.Total : 0;
    });

    private async Task<string> DownloadDrivePricesAsync(DrivePriceFile file, CancellationToken ct)
    {
        FileOperationProgressText = "Conferindo o pacote na pasta do Google Drive…";
        IsFileOperationIndeterminate = true;
        using var http = DrivePriceUpdateService.CreateClient();
        var service = new DrivePriceUpdateService(http);
        var path = await service.DownloadAsync(file, Path.Combine(GitHubAppInstaller.CacheDirectory, "drive-prices"),
            CreateDownloadProgress(), ct);
        ct.ThrowIfCancellationRequested();
        return path;
    }

    public Task ContinueGitHubUpdateAsync(string id)
    {
        _gitHubUpdateTask = ContinuePendingUpdateAsync(id);
        return _gitHubUpdateTask;
    }

    private async Task ContinuePendingUpdateAsync(string id)
    {
        string? downloadedPrices = null;
        await RunFileOperationAsync(async ct =>
        {
            try
            {
                var pending = await GitHubAppInstaller.ReadPendingAsync(id, ct);
                if (ApplicationVersion != GitHubUpdateValidation.ParseVersion(pending.AppVersion))
                    throw new InvalidDataException("A versão instalada não corresponde à atualização pendente.");
                if (pending.DrivePrices is { } file)
                {
                    if (!pending.UpdatePricesAfterRestart || pending.PriceUpdate is not null || pending.DeferredPrices is not null ||
                        !string.Equals(Path.GetFullPath(pending.DatabasePath), Path.GetFullPath(_calibrationService.Connections.DatabasePath),
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A atualização pendente não corresponde ao banco ou à seleção de preços.");
                    downloadedPrices = await DownloadDrivePricesAsync(file, ct);
                }
                else
                    // Compatibility for pending operations written by older versions.
                    await ApplyGitHubPricesAsync(pending, ct, refreshMissingPrices: pending.UpdatePricesAfterRestart);
                GitHubAppInstaller.Complete(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusText = "Programa atualizado. Atualização dos preços interrompida; use Atualizar pelo GitHub para tentar novamente.";
            }
            catch (Exception e) when (!AsyncCommandRuntime.IsCritical(e))
            {
                throw new IOException("O programa foi atualizado, mas a etapa dos preços não foi concluída. " +
                    "Use Atualizar pelo GitHub para tentar novamente. " + e.Message, e);
            }
        });
        if (downloadedPrices is not null && !_disposed)
            await ImportBackupAsync(pricesOnly: true, sourcePath: downloadedPrices);
    }

    private async Task ApplyGitHubPricesAsync(PendingGitHubUpdate pending, CancellationToken ct,
        bool refreshMissingPrices = false)
    {
        var official = new OfficialUpdateService(_calibrationService.Connections);
        if (!string.Equals(Path.GetFullPath(pending.DatabasePath), Path.GetFullPath(_calibrationService.Connections.DatabasePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O banco selecionado mudou. Consulte novamente as atualizações antes de importar.");
        // A legacy updater may have approved the ten-day compatibility manifest.
        // After installing, resolve the full window with the updated reader.
        if (refreshMissingPrices && pending.PriceUpdate is null)
        {
            FileOperationProgressText = "Programa atualizado. Consultando o pacote de preços…";
            using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
                { Timeout = Timeout.InfiniteTimeSpan };
            var prices = await new GitHubUpdateService(http).GetPricesAfterRestartAsync(ApplicationVersion,
                SqliteContractRepository.CurrentSchemaVersion, await official.GetTransferStatusAsync(ct), ct);
            if (prices is null)
            {
                StatusText = "Programa atualizado. Preços já atualizados neste banco.";
                return;
            }
            pending = pending with { DeferredPrices = prices };
        }
        if (pending.DeferredPrices is { } deferred)
        {
            GitHubUpdateValidation.Validate(deferred.Manifest);
            if (deferred.Tag != "precos" || pending.PriceUpdate is not null ||
                GitHubUpdateValidation.ParseVersion(deferred.Manifest.MinimumAppVersion) > ApplicationVersion)
                throw new InvalidDataException("Atualização de preços pendente incompatível com o programa instalado.");
            var package = deferred.Manifest.Update;
            GitHubUpdateService.EnsureSpace(Path.GetDirectoryName(pending.DatabasePath)!, package.ExpandedSize);
            GitHubUpdateService.EnsureSpace(GitHubAppInstaller.CacheDirectory, checked(package.Download.Size * 2));
            using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
                { Timeout = Timeout.InfiniteTimeSpan };
            var downloadProgress = new Progress<UpdateDownloadProgress>(p =>
            {
                FileOperationProgressText = p.Message;
                IsFileOperationIndeterminate = p.Message.StartsWith("Recompondo", StringComparison.Ordinal);
                OperationProgress = p.Total > 0 ? 100d * p.Received / p.Total : 0;
            });
            var path = await new GitHubUpdateService(http).DownloadAsync(deferred.Tag, package.Download,
                GitHubAppInstaller.CacheDirectory, downloadProgress, ct);
            pending = pending with { PriceUpdate = new(path, package), DeferredPrices = null };
        }
        if (pending.PriceUpdate is { } item)
        {
            var expectedPath = Path.Combine(GitHubAppInstaller.CacheDirectory, item.Package.Download.Sha256.ToLowerInvariant() + ".payload");
            if (!string.Equals(Path.GetFullPath(item.Path), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                item.Package.Manifest.Schema != OfficialUpdateService.PayloadSchemaVersion)
                throw new InvalidDataException("Pacote pendente incompatível.");
            await GitHubUpdateService.ValidatePackageAsync(item.Path, item.Package, ct);
        }
        if (pending.PriceUpdate is null) { StatusText = "Programa atualizado com sucesso."; return; }
        IsFileOperationIndeterminate = true;
        var progress = new Progress<string>(s => FileOperationProgressText = s);
        try
        {
            var downloaded = pending.PriceUpdate!;
            var result = await Task.Run(() => official.ImportAsync(downloaded.Path, progress, ct), ct);
            StatusText = $"Atualização concluída. Registros aplicados: {result.Applied:N0}; preservados: {result.Skipped:N0}.";
            File.Delete(downloaded.Path);
            File.Delete(downloaded.Path + ".verified");
        }
        finally { if (!_disposed) await RefreshAfterOfficialImportAsync(); }
    }
}
