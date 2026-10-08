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
                var notes = plan.App is null ? new GitHubReleaseNotes([], null) :
                    await downloads.GetReleaseNotesAsync(ApplicationVersion,
                        GitHubUpdateValidation.ParseVersion(plan.App.Version), ct);
                ct.ThrowIfCancellationRequested();
                var pricesRequireProgramUpdate = check.Prices is { } availablePrices &&
                    (GitHubUpdateValidation.ParseVersion(availablePrices.Manifest.MinimumAppVersion) > ApplicationVersion ||
                     availablePrices.Manifest.Schema > SqliteContractRepository.CurrentSchemaVersion);
                var preview = new GitHubUpdateWindow(plan, notes, pricesRequireProgramUpdate)
                    { Owner = Application.Current.MainWindow };
                if (preview.ShowDialog() != true) return;
                ct.ThrowIfCancellationRequested();
                plan = plan with
                {
                    App = preview.UpdateProgram ? plan.App : null,
                    Package = preview.UpdatePrices ? plan.Package : null
                };
                if (!plan.HasUpdates) return;
                if (plan.App is not null) GitHubAppInstaller.CheckDestination();
                var databasePath = _calibrationService.Connections.DatabasePath;
                if (plan.App is null && plan.Package is { } pricePackage)
                    GitHubUpdateService.EnsureSpace(Path.GetDirectoryName(databasePath)!, pricePackage.ExpandedSize);
                Directory.CreateDirectory(GitHubAppInstaller.CacheDirectory);
                var firstDownloadSize = plan.App?.File.Size ?? plan.Package?.Download.Size ?? 0;
                GitHubUpdateService.EnsureSpace(GitHubAppInstaller.CacheDirectory, checked(firstDownloadSize * 2));
                DownloadedPriceUpdate? downloadedPrices = null;
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
                // Install and restart before downloading prices. A slow or unavailable price
                // package must not delay installation of an already validated program update.
                if (plan.App is null && plan.Package is { } package)
                {
                    var path = await downloads.DownloadAsync(check.Prices!.Tag, package.Download, GitHubAppInstaller.CacheDirectory, progress, ct);
                    FileOperationProgressText = "Conferindo o manifesto do pacote de preços…";
                    await GitHubUpdateService.ValidatePackageAsync(path, package, ct);
                    downloadedPrices = new(path, package);
                }
                var pending = new PendingGitHubUpdate(databasePath,
                    plan.App?.Version ?? ApplicationVersion.ToString(3), downloadedPrices,
                    plan.App is not null && plan.Package is not null ? check.Prices : null,
                    UpdatePricesAfterRestart: preview.UpdatePrices);
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
                await ApplyGitHubPricesAsync(pending, ct, refreshMissingPrices: pending.UpdatePricesAfterRestart);
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

    private async Task ApplyGitHubPricesAsync(PendingGitHubUpdate pending, CancellationToken ct,
        bool refreshMissingPrices = false)
    {
        var official = new OfficialUpdateService(_calibrationService.Connections);
        if (!string.Equals(Path.GetFullPath(pending.DatabasePath), Path.GetFullPath(_calibrationService.Connections.DatabasePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O banco selecionado mudou. Consulte novamente as atualizações antes de importar.");
        if (refreshMissingPrices && pending.PriceUpdate is null && pending.DeferredPrices is null)
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
