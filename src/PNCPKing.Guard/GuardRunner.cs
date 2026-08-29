using System.Diagnostics;
using System.Text.Json;
using PNCPKing.Core.Models;

namespace PNCPKing.Guard;

internal sealed record GuardRunResult(string Message, GuardLocalStatus Status);

internal sealed class GuardCampaignChangedException : Exception
{
    public GuardCampaignChangedException() : base("A campanha foi substituída pelo PNCP King mestre.")
    {
    }
}

internal sealed record GuardStatusFile
{
    public required string WorkerId { get; init; }
    public required string CampaignId { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string State { get; init; }
    public required int Pending { get; init; }
    public required int CompleteAwaitingPackage { get; init; }
    public required int PublishedAwaitingAck { get; init; }
    public required int Acknowledged { get; init; }
    public required int FailedWaiting { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal sealed class GuardRunner
{
    private const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumRawPackageBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan MaximumPackageAge = TimeSpan.FromMinutes(10);
    private readonly GuardSettingsService _settingsService;
    private readonly GuardLog _log;

    public GuardRunner(GuardSettingsService settingsService, GuardLog log)
    {
        _settingsService = settingsService;
        _log = log;
    }

    public async Task<GuardRunResult> RunAsync(
        GuardSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TryLowerPriority();
        ValidateSettings(settings);
        var root = Path.GetFullPath(settings.DriveRoot!);
        var control = await ReadControlAsync(root, cancellationToken).ConfigureAwait(false);
        var planPath = ResolveCurrentPlanPath(settings, root, control);
        var plan = await GuardFileCodec.ReadJsonAsync<GuardWorkerPlan>(planPath, cancellationToken)
            .ConfigureAwait(false);
        ValidatePlan(settings, control, plan);

        var repository = new GuardRepository(_settingsService.DatabasePath);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var changed = await repository.ApplyPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            CleanupLocalOutbox();
            _log.Write($"Campanha {plan.CampaignId} carregada com {plan.Contracts.Count} contratação(ões).");
        }

        Directory.CreateDirectory(_settingsService.OutboxFolder);
        await ProcessAcksAsync(repository, root, cancellationToken).ConfigureAwait(false);
        await PublishPendingPackagesAsync(repository, root, cancellationToken).ConfigureAwait(false);

        using var kingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = MonitorPncpKingAsync(kingCancellation);
        var batchStarted = DateTimeOffset.UtcNow;
        var batchContracts = 0;
        var batchBytes = 0L;
        string? currentContract = null;
        var stoppedForKing = false;
        var campaignActive = true;
        try
        {
            while (true)
            {
                kingCancellation.Token.ThrowIfCancellationRequested();
                await EnsureCampaignActiveAsync(root, plan, kingCancellation.Token).ConfigureAwait(false);
                EnsureFreeSpace();
                var work = await repository.AcquireNextAsync(DateTimeOffset.UtcNow, kingCancellation.Token)
                    .ConfigureAwait(false);
                if (work is null)
                {
                    break;
                }

                currentContract = work.Contract.PncpId;
                progress?.Report($"Coletando {currentContract}…");
                try
                {
                    var snapshot = await CollectContractAsync(work.Contract, kingCancellation.Token)
                        .ConfigureAwait(false);
                    await EnsureCampaignActiveAsync(root, plan, kingCancellation.Token).ConfigureAwait(false);
                    await repository.SaveSnapshotAsync(snapshot, kingCancellation.Token).ConfigureAwait(false);
                    batchContracts++;
                    batchBytes += JsonSerializer.SerializeToUtf8Bytes(snapshot, GuardFormat.JsonOptions).LongLength;
                    currentContract = null;
                    if (batchContracts >= 100 || batchBytes >= MaximumRawPackageBytes ||
                        DateTimeOffset.UtcNow - batchStarted >= MaximumPackageAge)
                    {
                        await CreatePackageAsync(repository, plan, root, kingCancellation.Token).ConfigureAwait(false);
                        batchStarted = DateTimeOffset.UtcNow;
                        batchContracts = 0;
                        batchBytes = 0;
                    }
                }
                catch (GuardPncpException exception)
                {
                    var retry = exception.RetryAt ?? CalculateRetry(work.Attempts + 1);
                    await repository.MarkFailureAsync(
                            work.Contract.PncpId,
                            exception.Message,
                            retry,
                            kingCancellation.Token)
                        .ConfigureAwait(false);
                    _log.Write($"Falha em {work.Contract.PncpId}; nova tentativa em {retry:O}. {exception.Message}");
                    currentContract = null;
                }
                catch (OperationCanceledException) when (kingCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    var retry = CalculateRetry(work.Attempts + 1);
                    await repository.MarkFailureAsync(
                            work.Contract.PncpId,
                            exception.Message,
                            retry,
                            kingCancellation.Token)
                        .ConfigureAwait(false);
                    _log.Write($"Falha em {work.Contract.PncpId}; nova tentativa em {retry:O}. {exception.Message}");
                    currentContract = null;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && IsPncpKingRunning())
        {
            stoppedForKing = true;
            _log.Write("PNCP King abriu; a coleta foi interrompida no checkpoint seguro.");
        }
        catch (GuardCampaignChangedException)
        {
            campaignActive = false;
            _log.Write("A campanha foi substituída; dados ainda não publicados foram preservados somente até o próximo plano.");
        }
        finally
        {
            if (currentContract is not null)
            {
                await repository.ReturnToPendingAsync(currentContract).ConfigureAwait(false);
            }

            kingCancellation.Cancel();
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do monitor.
            }

            try
            {
                if (campaignActive)
                {
                    await CreatePackageAsync(repository, plan, root, CancellationToken.None).ConfigureAwait(false);
                    await PublishPendingPackagesAsync(repository, root, CancellationToken.None).ConfigureAwait(false);
                    await ProcessAcksAsync(repository, root, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _log.Write("Não foi possível fechar/publicar o último pacote: " + exception.Message);
            }
        }

        var status = await repository.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var message = !campaignActive
            ? "A campanha foi substituída; o novo plano será adotado no próximo ciclo."
            : stoppedForKing
            ? "Coleta pausada porque o PNCP King está aberto."
            : status.Pending == 0
                ? "Não há contratações elegíveis neste momento."
                : "Ciclo concluído; falhas transitórias aguardam a próxima tentativa.";
        await WriteStatusAsync(
                root,
                plan,
                status,
                !campaignActive ? "invalidated" : stoppedForKing ? "paused" : "idle",
                message)
            .ConfigureAwait(false);
        progress?.Report(message);
        return new GuardRunResult(message, status);
    }

    private static async Task<GuardContractSnapshot> CollectContractAsync(
        GuardPlanContract contract,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 1
        })
        {
            Timeout = TimeSpan.FromMinutes(6)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PNCPGuard/1.0 (+https://pncp.gov.br)");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var client = new GuardPncpClient(httpClient);
        var items = await client.GetItemsAsync(contract, cancellationToken).ConfigureAwait(false);

        return new GuardContractSnapshot
        {
            Contract = contract,
            CollectedAt = DateTimeOffset.UtcNow,
            Items = items,
            Results = []
        };
    }

    private async Task CreatePackageAsync(
        GuardRepository repository,
        GuardWorkerPlan plan,
        string root,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var ready = await repository.ReadReadySnapshotsAsync(100, cancellationToken).ConfigureAwait(false);
            if (ready.Count == 0)
            {
                return;
            }

            var selected = new List<GuardContractSnapshot>(ready.Count);
            var bytes = 0L;
            foreach (var snapshot in ready)
            {
                var size = JsonSerializer.SerializeToUtf8Bytes(snapshot, GuardFormat.JsonOptions).LongLength;
                if (selected.Count > 0 && bytes + size > MaximumRawPackageBytes)
                {
                    break;
                }

                selected.Add(snapshot);
                bytes += size;
            }

            var temporaryName = Guid.NewGuid().ToString("N") + GuardFormat.PackageExtension;
            var temporaryPath = Path.Combine(_settingsService.OutboxFolder, temporaryName);
            var manifest = await GuardFileCodec.WritePackageAsync(
                    temporaryPath,
                    plan.CampaignId,
                    plan.Worker.Id,
                    selected,
                    cancellationToken)
                .ConfigureAwait(false);
            var generatedPath = Path.ChangeExtension(temporaryPath, GuardFormat.PackageExtension);
            var finalLocalPath = Path.Combine(
                _settingsService.OutboxFolder,
                manifest.PackageId + GuardFormat.PackageExtension);
            File.Move(generatedPath, finalLocalPath, overwrite: false);
            var hash = await GuardFileCodec.ComputeFileSha256Async(finalLocalPath, cancellationToken)
                .ConfigureAwait(false);
            await repository.RecordPackageAsync(
                    manifest,
                    hash,
                    finalLocalPath,
                    selected.Select(snapshot => snapshot.Contract.PncpId).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishPendingPackagesAsync(repository, root, cancellationToken).ConfigureAwait(false);
            if (ready.Count < 100)
            {
                return;
            }
        }
    }

    private static async Task PublishPendingPackagesAsync(
        GuardRepository repository,
        string root,
        CancellationToken cancellationToken)
    {
        foreach (var package in await repository.ReadPackagesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (package.PublishedPath is not null && File.Exists(package.PublishedPath))
            {
                continue;
            }

            var directory = Path.Combine(root, "packages", package.CampaignId, package.WorkerId);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, package.PackageId + GuardFormat.PackageExtension);
            if (File.Exists(destination))
            {
                var existingHash = await GuardFileCodec.ComputeFileSha256Async(destination, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(existingHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Já existe pacote {package.PackageId} com outro checksum na pasta sincronizada.");
                }

                await repository.MarkPublishedAsync(package.PackageId, destination, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var partial = destination + ".partial";
            try
            {
                await using (var input = new FileStream(package.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var copiedHash = await GuardFileCodec.ComputeFileSha256Async(partial, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(copiedHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("O checksum mudou durante a publicação do pacote.");
                }

                _ = await GuardFileCodec.ReadPackageAsync(partial, cancellationToken).ConfigureAwait(false);
                File.Move(partial, destination, overwrite: false);
                await repository.MarkPublishedAsync(package.PackageId, destination, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }
    }

    private static async Task ProcessAcksAsync(
        GuardRepository repository,
        string root,
        CancellationToken cancellationToken)
    {
        foreach (var package in await repository.ReadPackagesAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = Path.Combine(root, "acks", package.CampaignId, package.PackageId + ".ack");
            if (!File.Exists(path))
            {
                continue;
            }

            GuardAck ack;
            try
            {
                ack = await GuardFileCodec.ReadJsonAsync<GuardAck>(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                continue;
            }

            if (ack.PackageId != package.PackageId || ack.CampaignId != package.CampaignId ||
                !string.Equals(ack.PackageSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (package.PublishedPath is not null && File.Exists(package.PublishedPath))
            {
                File.Delete(package.PublishedPath);
            }

            if (File.Exists(package.LocalPath))
            {
                File.Delete(package.LocalPath);
            }

            await repository.AcknowledgeAsync(package.PackageId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<GuardControl> ReadControlAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "control.json");
        var control = await GuardFileCodec.ReadJsonAsync<GuardControl>(path, cancellationToken).ConfigureAwait(false);
        if (control.Kind != GuardFormat.ControlKind || control.Version != GuardFormat.Version)
        {
            throw new InvalidDataException("control.json incompatível com esta versão do PNCP Guard.");
        }

        return control;
    }

    private static async Task EnsureCampaignActiveAsync(
        string root,
        GuardWorkerPlan plan,
        CancellationToken cancellationToken)
    {
        var control = await ReadControlAsync(root, cancellationToken).ConfigureAwait(false);
        if (control.CampaignId != plan.CampaignId || control.MasterId != plan.MasterId)
        {
            throw new GuardCampaignChangedException();
        }
    }

    private static string ResolveCurrentPlanPath(GuardSettings settings, string root, GuardControl control)
    {
        var assigned = control.Workers.FirstOrDefault(worker => worker.WorkerId == settings.WorkerId);
        if (assigned is not null)
        {
            return Path.GetFullPath(Path.Combine(root, assigned.PlanRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        return Path.GetFullPath(settings.PlanPath!);
    }

    private static void ValidateSettings(GuardSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WorkerId) || string.IsNullOrWhiteSpace(settings.PlanPath) ||
            string.IsNullOrWhiteSpace(settings.DriveRoot) || !Directory.Exists(settings.DriveRoot))
        {
            throw new InvalidOperationException("Configure o plano do trabalhador e a raiz local do Google Drive.");
        }
    }

    private static void ValidatePlan(GuardSettings settings, GuardControl control, GuardWorkerPlan plan)
    {
        if (plan.Kind != GuardFormat.PlanKind || plan.Version != GuardFormat.Version ||
            plan.Worker.Id != settings.WorkerId || plan.CampaignId != control.CampaignId ||
            plan.MasterId != control.MasterId)
        {
            throw new InvalidDataException("O plano não corresponde ao trabalhador ou à campanha ativa.");
        }
    }

    private void CleanupLocalOutbox()
    {
        if (!Directory.Exists(_settingsService.OutboxFolder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_settingsService.OutboxFolder))
        {
            if (path.EndsWith(GuardFormat.PackageExtension, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    private static void EnsureFreeSpace()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.GetPathRoot(local);
        if (!string.IsNullOrWhiteSpace(root) && new DriveInfo(root).AvailableFreeSpace < MinimumFreeBytes)
        {
            throw new IOException("A coleta foi pausada para preservar a reserva mínima de 2 GiB.");
        }
    }

    private static DateTimeOffset CalculateRetry(int attempts)
    {
        var minutes = Math.Min(24 * 60, 30 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6)));
        return DateTimeOffset.UtcNow.AddMinutes(minutes);
    }

    private static async Task MonitorPncpKingAsync(CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            if (IsPncpKingRunning())
            {
                cancellation.Cancel();
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token).ConfigureAwait(false);
        }
    }

    private static bool IsPncpKingRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting("Local\\PNCPKing.SingleInstance");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryLowerPriority()
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // O Guard continua se a política do Windows impedir a alteração.
        }
    }

    private static async Task WriteStatusAsync(
        string root,
        GuardWorkerPlan plan,
        GuardLocalStatus status,
        string state,
        string message)
    {
        var value = new GuardStatusFile
        {
            WorkerId = plan.Worker.Id,
            CampaignId = plan.CampaignId,
            UpdatedAt = DateTimeOffset.UtcNow,
            State = state,
            Pending = status.Pending,
            CompleteAwaitingPackage = status.Complete,
            PublishedAwaitingAck = status.Packaged,
            Acknowledged = status.Acknowledged,
            FailedWaiting = status.FailedWaiting,
            Message = message
        };
        await GuardFileCodec.WriteJsonAtomicAsync(
                Path.Combine(root, "status", plan.Worker.Id + ".json"),
                value,
                overwrite: true,
                CancellationToken.None)
            .ConfigureAwait(false);
    }
}
