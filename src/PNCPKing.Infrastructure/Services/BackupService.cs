using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed class BackupService(
    IContractRepository repository,
    IPerformanceTelemetry? performance = null)
{
    private const int CurrentArchiveFormatVersion = 2;
    private const long Fat32MaximumFileBytes = 4L * 1024 * 1024 * 1024 - 1;
    private const long ImportSafetyReserveBytes = 1L * 1024 * 1024 * 1024;
    private const long ArchiveSafetyReserveBytes = 64L * 1024 * 1024;
    private const int ProgressBufferBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly IPerformanceTelemetry _performance = performance ?? NullPerformanceTelemetry.Instance;

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await ExportAsync(
                destinationPath,
                BackupProfile.Full,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ExportAsync(
        string destinationPath,
        BackupProfile profile,
        CancellationToken cancellationToken = default)
    {
        await ExportAsync(destinationPath, profile, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BackupExportInspection> InspectExportAsync(
        string destinationPath,
        BackupProfile profile,
        CancellationToken cancellationToken = default)
    {
        destinationPath = NormalizeBackupPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        var databaseBytes = File.Exists(repository.DatabasePath)
            ? new FileInfo(repository.DatabasePath).Length
            : 0L;
        var snapshot = await ReadSnapshotMetadataAsync(repository.DatabasePath, cancellationToken)
            .ConfigureAwait(false);
        var evidence = await ReadReferencedEvidenceAssetsAsync(repository.DatabasePath, cancellationToken)
            .ConfigureAwait(false);
        var evidenceBytes = evidence.Aggregate(
            0L,
            (total, asset) => AddSaturated(total, asset.ByteLength));
        var stagingWorkingBytes = profile == BackupProfile.Compact
            ? AddSaturated(databaseBytes, databaseBytes, ImportSafetyReserveBytes)
            : AddSaturated(databaseBytes, ImportSafetyReserveBytes);
        var destinationRequiredBytes = AddSaturated(
            databaseBytes,
            evidenceBytes,
            ArchiveSafetyReserveBytes);
        var temporaryDirectoryRoot = Path.GetFullPath(Path.GetTempPath());
        var temporaryVolumeRoot = GetVolumeRoot(temporaryDirectoryRoot);
        var destinationVolumeRoot = GetVolumeRoot(destinationDirectory);
        var temporaryAvailable = GetAvailableBytes(temporaryVolumeRoot);
        var destinationAvailable = GetAvailableBytes(destinationVolumeRoot);
        var sameVolume = PathsEqual(temporaryVolumeRoot, destinationVolumeRoot);
        var stagingDirectoryRoot = temporaryDirectoryRoot;
        var stagingVolumeRoot = temporaryVolumeRoot;
        long stagingRequired;
        long effectiveDestinationRequired;
        if (sameVolume)
        {
            stagingRequired = AddSaturated(stagingWorkingBytes, destinationRequiredBytes);
            effectiveDestinationRequired = stagingRequired;
            destinationAvailable = temporaryAvailable;
        }
        else if (temporaryAvailable >= stagingWorkingBytes &&
                 destinationAvailable >= destinationRequiredBytes)
        {
            stagingRequired = stagingWorkingBytes;
            effectiveDestinationRequired = destinationRequiredBytes;
        }
        else
        {
            stagingDirectoryRoot = destinationDirectory;
            stagingVolumeRoot = destinationVolumeRoot;
            sameVolume = true;
            stagingRequired = AddSaturated(stagingWorkingBytes, destinationRequiredBytes);
            effectiveDestinationRequired = stagingRequired;
            temporaryAvailable = destinationAvailable;
        }

        var driveFormat = GetDriveFormat(destinationVolumeRoot);
        var exceedsFileLimit = string.Equals(driveFormat, "FAT32", StringComparison.OrdinalIgnoreCase) &&
                               AddSaturated(
                                   databaseBytes,
                                   evidenceBytes,
                                   ArchiveSafetyReserveBytes) > Fat32MaximumFileBytes;
        return new BackupExportInspection
        {
            Profile = profile,
            DestinationPath = destinationPath,
            StagingDirectoryRoot = stagingDirectoryRoot,
            StagingVolumeRoot = stagingVolumeRoot,
            DestinationVolumeRoot = destinationVolumeRoot,
            DatabaseBytes = databaseBytes,
            EvidenceBytes = evidenceBytes,
            ContractCount = snapshot.Contracts,
            ItemCount = snapshot.Items,
            ResultCount = snapshot.Results,
            StagingAvailableBytes = temporaryAvailable,
            DestinationAvailableBytes = destinationAvailable,
            StagingRequiredBytes = stagingRequired,
            DestinationRequiredBytes = effectiveDestinationRequired,
            SharesStagingAndDestinationVolume = sameVolume,
            DestinationDriveFormat = driveFormat,
            ExceedsDestinationFileLimit = exceedsFileLimit
        };
    }

    public async Task ExportAsync(
        string destinationPath,
        BackupProfile profile,
        IProgress<BackupExportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("backup", "export");
        destinationPath = NormalizeBackupPath(destinationPath);
        ReportExport(
            progress,
            BackupExportStage.Inspecting,
            1,
            "Verificando banco, evidências e espaço disponível…",
            isIndeterminate: true);
        var inspection = await InspectExportAsync(destinationPath, profile, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.ExceedsDestinationFileLimit)
        {
            throw new IOException(
                "A unidade de destino usa FAT32 e o backup pode ultrapassar o limite de 4 GiB. " +
                "Escolha uma unidade NTFS ou exFAT.");
        }
        if (!inspection.HasEnoughSpace)
        {
            throw new IOException(
                "Espaço insuficiente para exportar com segurança. " +
                $"Staging: {FormatBytes(inspection.StagingAvailableBytes)} livres, " +
                $"{FormatBytes(inspection.StagingRequiredBytes)} necessários. " +
                $"Destino: {FormatBytes(inspection.DestinationAvailableBytes)} livres, " +
                $"{FormatBytes(inspection.DestinationRequiredBytes)} necessários.");
        }

        ReportExport(
            progress,
            BackupExportStage.Checkpointing,
            4,
            "Consolidando o WAL antes do snapshot…",
            isIndeterminate: true);
        await repository.CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
        var temporaryDirectory = CreateExportStagingDirectory(inspection.StagingDirectoryRoot);
        var temporaryArchive = destinationPath + ".partial";
        try
        {
            var snapshotPath = Path.Combine(temporaryDirectory, "data.db");
            ReportExport(
                progress,
                BackupExportStage.Snapshotting,
                8,
                "Criando snapshot consistente do banco…",
                isIndeterminate: true);
            using (var snapshotSpan = _performance.Begin("backup", "export-snapshot"))
            {
                await CreateSnapshotAsync(repository.DatabasePath, snapshotPath, cancellationToken)
                    .ConfigureAwait(false);
                snapshotSpan.Complete(bytes: new FileInfo(snapshotPath).Length);
            }

            if (profile == BackupProfile.Compact)
            {
                ReportExport(
                    progress,
                    BackupExportStage.Compacting,
                    30,
                    "Removendo o cache reconstruível e compactando o snapshot…",
                    isIndeterminate: true);
                using var compactSpan = _performance.Begin("backup", "export-compact-prune");
                await CompactSnapshotAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
                compactSpan.Complete(bytes: new FileInfo(snapshotPath).Length);
            }

            ReportExport(
                progress,
                BackupExportStage.CheckingIntegrity,
                45,
                "Verificando integralmente o SQLite na origem…",
                isIndeterminate: true);
            DateTimeOffset integrityValidatedAt;
            using (var integritySpan = _performance.Begin("backup", "export-integrity"))
            {
                await ValidateDatabaseAsync(
                    snapshotPath,
                    SqliteContractRepository.CurrentSchemaVersion,
                    checkIntegrity: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                integrityValidatedAt = DateTimeOffset.UtcNow;
                integritySpan.Complete(bytes: new FileInfo(snapshotPath).Length);
            }

            var snapshotMetadata = await ReadSnapshotMetadataAsync(snapshotPath, cancellationToken)
                .ConfigureAwait(false);
            var evidenceAssets = await ReadReferencedEvidenceAssetsAsync(snapshotPath, cancellationToken)
                .ConfigureAwait(false);
            var dataFolder = Path.GetDirectoryName(repository.DatabasePath)!;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }

            string databaseHash;
            using (var archiveSpan = _performance.Begin("backup", "export-archive-hash"))
            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                var databaseEntry = archive.CreateEntry("data.db", CompressionLevel.Fastest);
                await using (var databaseOutput = databaseEntry.Open())
                {
                    databaseHash = await CopyFileWithHashAsync(
                            snapshotPath,
                            databaseOutput,
                            (processed, total) => ReportExport(
                                progress,
                                BackupExportStage.ArchivingDatabase,
                                Scale(processed, total, 55, 92),
                                $"Compactando banco: {FormatBytes(processed)} de {FormatBytes(total)}…",
                                processed,
                                total),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                long evidenceProcessed = 0;
                var evidenceTotal = evidenceAssets.Aggregate(
                    0L,
                    (total, asset) => AddSaturated(total, asset.ByteLength));
                foreach (var asset in evidenceAssets)
                {
                    var source = ResolveEvidencePath(dataFolder, asset.RelativePath);
                    var entry = archive.CreateEntry(
                        $"internet-evidence/{asset.Sha256}.png",
                        CompressionLevel.Fastest);
                    await using var output = entry.Open();
                    var copied = await CopyFileWithHashAsync(
                            source,
                            output,
                            (processed, _) => ReportExport(
                                progress,
                                BackupExportStage.ArchivingEvidence,
                                Scale(evidenceProcessed + processed, evidenceTotal, 92, 98),
                                $"Incluindo evidências: {FormatBytes(evidenceProcessed + processed)} de " +
                                $"{FormatBytes(evidenceTotal)}…",
                                evidenceProcessed + processed,
                                evidenceTotal),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var copiedLength = new FileInfo(source).Length;
                    if (copiedLength != asset.ByteLength ||
                        !FixedTimeHexEquals(copied, asset.Sha256))
                    {
                        throw new InvalidDataException(
                            $"A evidência {asset.Sha256} mudou durante a exportação.");
                    }

                    evidenceProcessed = AddSaturated(evidenceProcessed, copiedLength);
                }

                var manifest = new DatasetManifest
                {
                    ArchiveFormatVersion = CurrentArchiveFormatVersion,
                    SchemaVersion = SqliteContractRepository.CurrentSchemaVersion,
                    AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    StartDate = snapshotMetadata.StartDate,
                    EndDate = snapshotMetadata.EndDate,
                    Scope = snapshotMetadata.Scope,
                    ContractCount = snapshotMetadata.Contracts,
                    ItemCount = snapshotMetadata.Items,
                    ResultCount = snapshotMetadata.Results,
                    CreatedAt = DateTimeOffset.UtcNow,
                    DatabaseSha256 = databaseHash,
                    DatabaseBytes = new FileInfo(snapshotPath).Length,
                    DatabaseIntegrityValidatedAtExport = true,
                    DatabaseIntegrityKind = "PRAGMA integrity_check",
                    DatabaseIntegrityValidatedAt = integrityValidatedAt,
                    EvidenceAssets = evidenceAssets
                        .Select(asset => new EvidenceAssetManifest
                        {
                            Sha256 = asset.Sha256,
                            ArchivePath = $"internet-evidence/{asset.Sha256}.png",
                            ByteLength = asset.ByteLength
                        })
                        .ToArray(),
                    BackupProfile = profile,
                    ContainsPriceCache = profile == BackupProfile.Full &&
                                         snapshotMetadata.PriceCacheContracts > 0,
                    PriceCacheContractCount = snapshotMetadata.PriceCacheContracts,
                    PriceCacheItemCount = snapshotMetadata.Items,
                    PriceCacheResultCount = snapshotMetadata.Results
                };
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestOutput = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(
                        manifestOutput,
                        manifest,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                archiveSpan.Complete(bytes: AddSaturated(
                    new FileInfo(snapshotPath).Length,
                    evidenceTotal));
            }

            File.Move(temporaryArchive, destinationPath, true);
            ReportExport(
                progress,
                BackupExportStage.Completed,
                100,
                "Backup validado e concluído.",
                canCancel: false);
            span.Complete(bytes: new FileInfo(destinationPath).Length);
        }
        catch
        {
            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }

            throw;
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    public async Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        return await ImportAsync(sourcePath, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupInspection> InspectAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("O backup informado não foi encontrado.", sourcePath);
        }

        DatasetManifest manifest;
        long databaseBytes;
        using (var archive = ZipFile.OpenRead(sourcePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("O backup não contém manifest.json.");
            var databaseEntry = archive.GetEntry("data.db")
                ?? throw new InvalidDataException("O backup não contém data.db.");
            await using var manifestStream = manifestEntry.Open();
            manifest = await JsonSerializer.DeserializeAsync<DatasetManifest>(
                           manifestStream,
                           JsonOptions,
                           cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("Manifesto inválido.");
            databaseBytes = databaseEntry.Length;
        }

        if (manifest.SchemaVersion is < 1 or > SqliteContractRepository.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Versão de banco incompatível: {manifest.SchemaVersion}. " +
                $"Versões aceitas: 1 a {SqliteContractRepository.CurrentSchemaVersion}.");
        }

        ValidateArchiveFormat(manifest);

        if (manifest.DatabaseBytes is { } declaredBytes && declaredBytes != databaseBytes)
        {
            throw new InvalidDataException(
                "O tamanho do banco não corresponde ao valor registrado no manifesto.");
        }

        var manifestAssets = manifest.EvidenceAssets ?? [];
        if (manifestAssets
            .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("O backup contém evidências duplicadas no manifesto.");
        }
        foreach (var asset in manifestAssets)
        {
            ValidateEvidenceManifestEntry(asset);
        }

        using (var archive = ZipFile.OpenRead(sourcePath))
        {
            foreach (var asset in manifestAssets)
            {
                var entry = archive.GetEntry(asset.ArchivePath)
                            ?? throw new InvalidDataException(
                                $"O backup não contém a evidência {asset.Sha256}.");
                if (entry.Length != asset.ByteLength)
                {
                    throw new InvalidDataException(
                        $"O tamanho da evidência {asset.Sha256} não corresponde ao manifesto.");
                }
            }
        }

        var evidenceBytes = manifestAssets.Aggregate(
            0L,
            (total, asset) => AddSaturated(total, asset.ByteLength));
        var temporaryRoot = GetVolumeRoot(Path.GetFullPath(Path.GetTempPath()));
        var dataDirectory = Path.GetDirectoryName(repository.DatabasePath)!;
        var dataRoot = GetVolumeRoot(dataDirectory);
        var existingDatabaseBytes = File.Exists(repository.DatabasePath)
            ? new FileInfo(repository.DatabasePath).Length
            : 0L;
        var requiresMigration = manifest.SchemaVersion < SqliteContractRepository.CurrentSchemaVersion;
        var migrationAllowance = requiresMigration
            ? Math.Max(ImportSafetyReserveBytes, databaseBytes / 2)
            : 0L;
        var dataRequired = AddSaturated(
            databaseBytes,
            evidenceBytes,
            evidenceBytes,
            ImportSafetyReserveBytes,
            migrationAllowance);
        var dataAvailable = GetAvailableBytes(dataRoot);

        return new BackupInspection
        {
            SourcePath = Path.GetFullPath(sourcePath),
            SchemaVersion = manifest.SchemaVersion,
            Profile = manifest.BackupProfile,
            ArchiveBytes = new FileInfo(sourcePath).Length,
            DatabaseBytes = databaseBytes,
            ExistingDatabaseBytes = existingDatabaseBytes,
            TemporaryRoot = temporaryRoot,
            DataRoot = dataRoot,
            TemporaryAvailableBytes = GetAvailableBytes(temporaryRoot),
            DataAvailableBytes = dataAvailable,
            TemporaryRequiredBytes = 0,
            DataRequiredBytes = dataRequired,
            SharesTemporaryAndDataVolume = PathsEqual(temporaryRoot, dataRoot),
            ContractCount = manifest.ContractCount,
            ItemCount = manifest.ItemCount,
            ResultCount = manifest.ResultCount,
            EvidenceBytes = evidenceBytes,
            EvidenceCount = manifestAssets.Count,
            RequiresMigration = requiresMigration,
            StagingRoot = dataDirectory,
            StagingAvailableBytes = dataAvailable,
            StagingRequiredBytes = dataRequired
        };
    }

    public async Task<string> ImportAsync(
        string sourcePath,
        IProgress<BackupImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var span = _performance.Begin("backup", "import");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Report(progress, BackupImportStage.Inspecting, 1, "Inspecionando o arquivo e o espaço disponível…");
        CleanupStaleTemporaryDirectories(TimeSpan.FromMinutes(5));
        CleanupStaleDatabaseStagingDirectories(TimeSpan.FromHours(24));
        var inspection = await InspectAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!inspection.HasEnoughSpace)
        {
            throw new IOException(
                "Espaço insuficiente para importar com segurança. " +
                $"Unidade do banco: {FormatBytes(inspection.StagingAvailableBytes)} livres, " +
                $"{FormatBytes(inspection.StagingRequiredBytes)} necessários.");
        }

        var temporaryDirectory = CreateImportStagingDirectory();
        try
        {
            var importedDatabase = Path.Combine(temporaryDirectory, "data.db");
            var stagedEvidenceFolder = Path.Combine(temporaryDirectory, "internet-evidence");
            DatasetManifest manifest;
            string actualHash;
            using var extractionSpan = _performance.Begin("backup", "import-extraction");
            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var manifestEntry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("O backup não contém manifest.json.");
                var databaseEntry = archive.GetEntry("data.db")
                    ?? throw new InvalidDataException("O backup não contém data.db.");
                await using (var manifestStream = manifestEntry.Open())
                {
                    manifest = await JsonSerializer.DeserializeAsync<DatasetManifest>(
                                   manifestStream,
                                   JsonOptions,
                                   cancellationToken).ConfigureAwait(false)
                               ?? throw new InvalidDataException("Manifesto inválido.");
                }

                if (manifest.SchemaVersion is < 1 or > SqliteContractRepository.CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Versão de banco incompatível: {manifest.SchemaVersion}. " +
                        $"Versões aceitas: 1 a {SqliteContractRepository.CurrentSchemaVersion}.");
                }

                ValidateArchiveFormat(manifest);
                var manifestAssets = manifest.EvidenceAssets ?? [];
                if (manifestAssets
                    .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() != 1))
                {
                    throw new InvalidDataException("O backup contém evidências duplicadas no manifesto.");
                }

                foreach (var asset in manifestAssets)
                {
                    ValidateEvidenceManifestEntry(asset);
                }

                var extractionTotal = manifestAssets.Aggregate(
                    databaseEntry.Length,
                    (total, asset) => AddSaturated(total, asset.ByteLength));

                await using var source = databaseEntry.Open();
                await using var destination = new FileStream(
                    importedDatabase,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    ProgressBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                actualHash = await CopyWithProgressAsync(
                        source,
                        destination,
                        databaseEntry.Length,
                        (processed, total) => Report(
                            progress,
                            BackupImportStage.Extracting,
                            Scale(processed, extractionTotal, 5, 40),
                            $"Descompactando o banco: {FormatBytes(processed)} de {FormatBytes(total)}…",
                            processed,
                            extractionTotal),
                        cancellationToken)
                    .ConfigureAwait(false);

                long extractedBytes = databaseEntry.Length;
                foreach (var asset in manifestAssets)
                {
                    var entry = archive.GetEntry(asset.ArchivePath)
                                ?? throw new InvalidDataException(
                                    $"O backup não contém a evidência {asset.Sha256}.");
                    Directory.CreateDirectory(stagedEvidenceFolder);
                    var stagedPath = Path.Combine(stagedEvidenceFolder, $"{asset.Sha256}.png");
                    await using var entryStream = entry.Open();
                    await using var staged = new FileStream(
                        stagedPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        ProgressBufferBytes,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var evidenceHash = await CopyWithProgressAsync(
                            entryStream,
                            staged,
                            entry.Length,
                            (processed, _) => Report(
                                progress,
                                BackupImportStage.Extracting,
                                Scale(extractedBytes + processed, extractionTotal, 5, 40),
                                $"Descompactando evidências: " +
                                $"{FormatBytes(extractedBytes + processed)} de " +
                                $"{FormatBytes(extractionTotal)}…",
                                extractedBytes + processed,
                                extractionTotal),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (entry.Length != asset.ByteLength ||
                        !FixedTimeHexEquals(evidenceHash, asset.Sha256))
                    {
                        throw new InvalidDataException(
                            $"A evidência {asset.Sha256} não corresponde ao manifesto.");
                    }

                    extractedBytes = AddSaturated(extractedBytes, entry.Length);
                }
            }
            extractionSpan.Complete(bytes: AddSaturated(
                inspection.DatabaseBytes,
                inspection.EvidenceBytes));

            Report(progress, BackupImportStage.VerifyingChecksum, 43, "Checksum calculado durante a extração.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(manifest.DatabaseSha256)))
            {
                throw new InvalidDataException("O checksum do banco não corresponde ao manifesto.");
            }

            if (manifest.DatabaseIntegrityValidatedAtExport == true)
            {
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    45,
                    "Backup validado integralmente na origem; confirmando a versão interna…");
                using var validationSpan = _performance.Begin("backup", "import-origin-validation");
                await ValidateDatabaseAsync(
                    importedDatabase,
                    manifest.SchemaVersion,
                    checkIntegrity: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                validationSpan.Complete(bytes: inspection.DatabaseBytes);
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    58,
                    "Integridade confirmada na origem e protegida pelo checksum SHA-256.");
            }
            else
            {
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    45,
                    "Backup legado; verificando integralmente o SQLite neste computador…");
                using var integritySpan = _performance.Begin("backup", "import-full-integrity");
                await ValidateDatabaseAsync(
                    importedDatabase,
                    manifest.SchemaVersion,
                    checkIntegrity: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                integritySpan.Complete(bytes: inspection.DatabaseBytes);
                Report(progress, BackupImportStage.CheckingIntegrity, 58, "Integridade local confirmada.");
            }

            var databaseAssets = await ReadReferencedEvidenceAssetsAsync(
                importedDatabase,
                cancellationToken).ConfigureAwait(false);
            var manifestHashes = (manifest.EvidenceAssets ?? [])
                .Select(asset => asset.Sha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!databaseAssets
                .Select(asset => asset.Sha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(manifestHashes))
            {
                throw new InvalidDataException(
                    "O manifesto não corresponde às evidências referenciadas pelo banco.");
            }

            // Migrate an older, otherwise valid PNCP King snapshot while it is
            // still isolated. The live database is not touched until every
            // migration and integrity check succeeds.
            if (manifest.SchemaVersion < SqliteContractRepository.CurrentSchemaVersion)
            {
                Report(
                    progress,
                    BackupImportStage.Migrating,
                    60,
                    $"Migrando o banco do esquema {manifest.SchemaVersion} para " +
                    $"{SqliteContractRepository.CurrentSchemaVersion}…");
                using (var migrationSpan = _performance.Begin("backup", "import-migration"))
                {
                    var importedRepository = new SqliteContractRepository(importedDatabase);
                    await importedRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    SqliteConnection.ClearAllPools();
                    migrationSpan.Complete(bytes: inspection.DatabaseBytes);
                }
                Report(
                    progress,
                    BackupImportStage.CheckingIntegrity,
                    70,
                    "Migração concluída; verificando novamente a integridade…");
                using var integritySpan = _performance.Begin("backup", "import-full-integrity");
                await ValidateDatabaseAsync(
                    importedDatabase,
                    SqliteContractRepository.CurrentSchemaVersion,
                    checkIntegrity: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                integritySpan.Complete(bytes: inspection.DatabaseBytes);
            }

            if (manifest.BackupProfile == BackupProfile.Compact)
            {
                await DisableImportedCompactCacheAsync(importedDatabase, cancellationToken).ConfigureAwait(false);
            }

            var dataFolder = Path.GetDirectoryName(repository.DatabasePath)!;
            Report(progress, BackupImportStage.InstallingEvidence, 80, "Validando e instalando evidências…");
            await InstallStagedEvidenceAsync(
                stagedEvidenceFolder,
                dataFolder,
                manifestHashes,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await repository.CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            var recoverableBackup = BuildRecoveryPath();
            DeleteSidecar(repository.DatabasePath, "-wal");
            DeleteSidecar(repository.DatabasePath, "-shm");
            var recoveryMoved = false;
            var replacementStarted = false;
            using var installationSpan = _performance.Begin("backup", "import-activation");
            try
            {
                Report(
                    progress,
                    BackupImportStage.PreservingCurrentDatabase,
                    86,
                    "Ativando o banco por troca atômica; cancelamento temporariamente indisponível…",
                    isIndeterminate: true,
                    canCancel: false);
                if (File.Exists(repository.DatabasePath))
                {
                    File.Move(repository.DatabasePath, recoverableBackup);
                    recoveryMoved = true;
                }

                Report(
                    progress,
                    BackupImportStage.InstallingDatabase,
                    93,
                    "Instalando o banco validado…",
                    isIndeterminate: true,
                    canCancel: false);
                File.Move(importedDatabase, repository.DatabasePath);
                replacementStarted = true;
                SqliteConnection.ClearAllPools();
                await repository.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                installationSpan.Complete(bytes: inspection.DatabaseBytes);
            }
            catch (Exception activationException)
            {
                installationSpan.Fail(activationException);
                using var rollbackSpan = _performance.Begin("backup", "import-rollback");
                try
                {
                    SqliteConnection.ClearAllPools();
                    DeleteSidecar(repository.DatabasePath, "-wal");
                    DeleteSidecar(repository.DatabasePath, "-shm");
                    if (replacementStarted && File.Exists(repository.DatabasePath))
                    {
                        File.Delete(repository.DatabasePath);
                    }

                    if (recoveryMoved && File.Exists(recoverableBackup))
                    {
                        File.Move(recoverableBackup, repository.DatabasePath);
                        SqliteConnection.ClearAllPools();
                        await repository.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    rollbackSpan.Complete();
                }
                catch (Exception rollbackException)
                {
                    rollbackSpan.Fail(rollbackException);
                    throw new AggregateException(
                        "A ativação do backup falhou e o banco anterior não pôde ser restaurado automaticamente.",
                        activationException,
                        rollbackException);
                }

                throw;
            }

            try
            {
                CleanupOrphanEvidence(dataFolder, manifestHashes);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The database is already active and confirmed. Orphan evidence
                // is safe to leave for a later cleanup attempt.
            }

            Report(
                progress,
                BackupImportStage.Completed,
                100,
                "Backup importado e aberto com sucesso.",
                canCancel: false);

            span.Complete(bytes: inspection.DatabaseBytes);
            return recoveryMoved ? recoverableBackup : string.Empty;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    public static int CleanupStaleTemporaryDirectories(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        var root = Path.Combine(Path.GetTempPath(), "PNCPKing");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - maximumAge;
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(path) >= threshold)
                {
                    continue;
                }

                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Another PNCP King process may still own this staging folder.
            }
        }

        return removed;
    }

    public IReadOnlyList<BackupRecoveryInfo> GetRecoveryBackups()
    {
        var databasePath = Path.GetFullPath(repository.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var pattern = $"{Path.GetFileName(databasePath)}.before-import-*.bak";
        return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(info => IsRecoveryBackupPath(info.FullName))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new BackupRecoveryInfo(
                info.FullName,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToArray();
    }

    public (int Count, long Bytes) DeleteRecoveryBackups()
    {
        var recoveries = GetRecoveryBackups();
        var deleted = 0;
        long bytes = 0;
        foreach (var recovery in recoveries)
        {
            if (!IsRecoveryBackupPath(recovery.Path))
            {
                continue;
            }

            File.Delete(recovery.Path);
            deleted++;
            bytes = AddSaturated(bytes, recovery.Bytes);
        }

        return (deleted, bytes);
    }

    private static async Task CreateSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // This database lives in a short-lived staging directory. Pooling
            // would keep data.db open on Windows after Dispose and make the
            // successful export look like a failure during cleanup.
            Pooling = false
        }.ToString();
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task CompactSnapshotAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var nationalStatisticsTriggers = new List<string>();
        await using (var triggerDefinitions = connection.CreateCommand())
        {
            triggerDefinitions.CommandText = """
                SELECT sql
                  FROM sqlite_master
                 WHERE type = 'trigger'
                   AND name IN (
                       'national_price_statistics_insert',
                       'national_price_statistics_delete',
                       'national_price_statistics_update')
                 ORDER BY name;
                """;
            await using var reader = await triggerDefinitions.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nationalStatisticsTriggers.Add(reader.GetString(0));
            }
        }

        if (nationalStatisticsTriggers.Count != 3)
        {
            throw new InvalidDataException(
                "Os triggers de estatísticas do índice nacional não foram encontrados no snapshot.");
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DROP TRIGGER IF EXISTS items_fts_insert;
                DROP TRIGGER IF EXISTS items_fts_delete;
                DROP TRIGGER IF EXISTS items_fts_update;
                DROP TRIGGER IF EXISTS dataset_statistics_item_insert;
                DROP TRIGGER IF EXISTS dataset_statistics_item_delete;
                DROP TRIGGER IF EXISTS dataset_statistics_result_insert;
                DROP TRIGGER IF EXISTS dataset_statistics_result_delete;
                DROP TRIGGER IF EXISTS national_price_statistics_insert;
                DROP TRIGGER IF EXISTS national_price_statistics_delete;
                DROP TRIGGER IF EXISTS national_price_statistics_update;
                DROP TABLE IF EXISTS items_fts;
                DELETE FROM item_results;
                DELETE FROM items;
                DELETE FROM contract_item_snapshots;
                DELETE FROM price_cache_contracts;
                CREATE VIRTUAL TABLE items_fts USING fts5(
                    search_text,
                    content='items',
                    content_rowid='rowid',
                    tokenize='unicode61 remove_diacritics 2',
                    prefix='2 3'
                );
                CREATE TRIGGER items_fts_insert AFTER INSERT ON items BEGIN
                    INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
                END;
                CREATE TRIGGER items_fts_delete AFTER DELETE ON items BEGIN
                    INSERT INTO items_fts(items_fts, rowid, search_text)
                    VALUES('delete', old.rowid, old.search_text);
                END;
                CREATE TRIGGER items_fts_update AFTER UPDATE OF search_text ON items BEGIN
                    INSERT INTO items_fts(items_fts, rowid, search_text)
                    VALUES('delete', old.rowid, old.search_text);
                    INSERT INTO items_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
                END;
                CREATE TRIGGER dataset_statistics_item_insert
                AFTER INSERT ON items BEGIN
                    UPDATE dataset_statistics
                       SET item_count = item_count + 1,
                           updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                     WHERE id = 1;
                END;
                CREATE TRIGGER dataset_statistics_item_delete
                AFTER DELETE ON items BEGIN
                    UPDATE dataset_statistics
                       SET item_count = MAX(0, item_count - 1),
                           updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                     WHERE id = 1;
                END;
                CREATE TRIGGER dataset_statistics_result_insert
                AFTER INSERT ON item_results BEGIN
                    UPDATE dataset_statistics
                       SET result_count = result_count + 1,
                           updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                     WHERE id = 1;
                END;
                CREATE TRIGGER dataset_statistics_result_delete
                AFTER DELETE ON item_results BEGIN
                    UPDATE dataset_statistics
                       SET result_count = MAX(0, result_count - 1),
                           updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                     WHERE id = 1;
                END;
                UPDATE dataset_statistics
                   SET item_count = 0, result_count = 0,
                       updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                 WHERE id = 1;
                UPDATE price_cache_control
                   SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                       last_error = '', authorized_at = NULL, last_started_at = NULL,
                       last_completed_at = NULL,
                       updated_at = $now
                 WHERE id = 1;
                UPDATE national_price_index_control
                   SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                       window_start = NULL, window_end = NULL,
                       authorized_at = NULL, last_started_at = NULL, last_completed_at = NULL,
                       prepared_window_start = NULL, prepared_window_end = NULL,
                       eligible_item_count = 0, completed_item_count = 0,
                       priced_item_count = 0, result_row_count = 0,
                       pending_contract_count = 0, failed_contract_count = 0,
                       statistics_suspended = 0, last_error = '', updated_at = $now
                 WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$disabled", (int)PriceCacheStatus.Disabled);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var triggerSql in nationalStatisticsTriggers)
            {
                await using var recreate = connection.CreateCommand();
                recreate.Transaction = (SqliteTransaction)transaction;
                recreate.CommandText = triggerSql;
                await recreate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DisableImportedCompactCacheAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM price_cache_contracts;
            UPDATE price_cache_control
               SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                   last_error = '', authorized_at = NULL, last_started_at = NULL,
                   last_completed_at = NULL, updated_at = $now
             WHERE id = 1;
            UPDATE national_price_index_control
               SET authorized = 0, enabled = 0, paused = 0, status = $disabled,
                   window_start = NULL, window_end = NULL,
                   authorized_at = NULL, last_started_at = NULL, last_completed_at = NULL,
                   prepared_window_start = NULL, prepared_window_end = NULL,
                   eligible_item_count = 0, completed_item_count = 0,
                   priced_item_count = 0, result_row_count = 0,
                   pending_contract_count = 0, failed_contract_count = 0,
                   last_error = '', updated_at = $now
             WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$disabled", (int)PriceCacheStatus.Disabled);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(
            databasePath,
            SqliteContractRepository.CurrentSchemaVersion,
            checkIntegrity: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SnapshotMetadata> ReadSnapshotMetadataAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.start_date, d.end_date, d.scope_kind, d.scope_uf,
                   s.contract_count, s.item_count, s.result_count,
                   (SELECT COUNT(*) FROM price_cache_contracts)
              FROM dataset_statistics s
              LEFT JOIN dataset d ON d.id = 1
             WHERE s.id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("As estatísticas materializadas do snapshot não foram encontradas.");
        }

        var start = ParseOptionalDate(reader, 0);
        var end = ParseOptionalDate(reader, 1);
        var scopeKind = reader.IsDBNull(2) ? GeoScopeKind.All : (GeoScopeKind)reader.GetInt32(2);
        var scope = scopeKind switch
        {
            GeoScopeKind.State => GeoScope.State(reader.IsDBNull(3) ? "SP" : reader.GetString(3)),
            GeoScopeKind.Southeast => GeoScope.Southeast,
            _ => GeoScope.All
        };
        return new SnapshotMetadata(
            start,
            end,
            scope.ToString(),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private static async Task ValidateDatabaseAsync(
        string path,
        int expectedSchemaVersion,
        bool checkIntegrity,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (checkIntegrity)
        {
            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Falha de integridade do SQLite: {result}");
            }
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        var schemaVersion = Convert.ToInt32(
            await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (schemaVersion != expectedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Versão interna incompatível: {schemaVersion}. Esperada no manifesto: {expectedSchemaVersion}.");
        }
    }

    private static async Task<IReadOnlyList<EvidenceAssetRow>> ReadReferencedEvidenceAssetsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var table = connection.CreateCommand();
        table.CommandText = """
            SELECT COUNT(*)
              FROM sqlite_master
             WHERE type = 'table' AND name = 'quotation_internet_evidence_assets';
            """;
        if (Convert.ToInt32(
                await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.sha256, a.relative_path, a.byte_length
              FROM quotation_internet_evidence_assets a
             WHERE a.sha256 IN (
                    SELECT price_image_sha256 FROM quotation_internet_price_drafts
                     WHERE price_image_sha256 IS NOT NULL
                    UNION
                    SELECT tax_id_image_sha256 FROM quotation_internet_price_drafts
                     WHERE tax_id_image_sha256 IS NOT NULL
                    UNION
                    SELECT price_image_sha256 FROM quotation_internet_price_evidence
                    UNION
                    SELECT tax_id_image_sha256 FROM quotation_internet_price_evidence)
             ORDER BY a.sha256;
            """;
        var result = new List<EvidenceAssetRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new EvidenceAssetRow(
                reader.GetString(0).ToLowerInvariant(),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        return result;
    }

    private static void ValidateEvidenceManifestEntry(EvidenceAssetManifest asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Sha256) ||
            asset.Sha256.Length != 64 ||
            asset.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
            asset.ByteLength <= 0 ||
            string.IsNullOrWhiteSpace(asset.ArchivePath) ||
            !string.Equals(
                asset.ArchivePath,
                $"internet-evidence/{asset.Sha256}.png",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O manifesto contém uma evidência inválida.");
        }
    }

    private static void ValidateArchiveFormat(DatasetManifest manifest)
    {
        if (manifest.ArchiveFormatVersion is <= 0 or > CurrentArchiveFormatVersion)
        {
            throw new InvalidDataException(
                $"Versão do formato de backup incompatível: {manifest.ArchiveFormatVersion}. " +
                $"Máxima aceita: {CurrentArchiveFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DatabaseSha256) ||
            manifest.DatabaseSha256.Length != 64 ||
            manifest.DatabaseSha256.Any(character => !Uri.IsHexDigit(character)) ||
            manifest.DatabaseBytes is < 0 ||
            manifest.ContractCount < 0 || manifest.ItemCount < 0 || manifest.ResultCount < 0)
        {
            throw new InvalidDataException("O manifesto contém metadados de banco inválidos.");
        }
    }

    private static string ResolveEvidencePath(string dataFolder, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("O caminho de uma evidência não pode ser absoluto.");
        }

        var evidenceRoot = Path.GetFullPath(Path.Combine(dataFolder, "internet-evidence"));
        var fullPath = Path.GetFullPath(
            Path.Combine(dataFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = evidenceRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O caminho de uma evidência saiu da pasta permitida.");
        }

        return fullPath;
    }

    private static async Task InstallStagedEvidenceAsync(
        string stagedFolder,
        string dataFolder,
        IReadOnlySet<string> hashes,
        CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return;
        }

        var destinationFolder = Path.Combine(dataFolder, "internet-evidence");
        Directory.CreateDirectory(destinationFolder);
        foreach (var hash in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(stagedFolder, $"{hash}.png");
            var destination = Path.Combine(destinationFolder, $"{hash}.png");
            var temporary = destination + ".importing";
            try
            {
                File.Copy(source, temporary, overwrite: true);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        await Task.CompletedTask;
    }

    private static void CleanupOrphanEvidence(
        string dataFolder,
        IReadOnlySet<string> referencedHashes)
    {
        var evidenceFolder = Path.Combine(dataFolder, "internet-evidence");
        if (!Directory.Exists(evidenceFolder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(evidenceFolder, "*.png", SearchOption.TopDirectoryOnly))
        {
            if (!referencedHashes.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                File.Delete(path);
            }
        }
    }

    private static string CreateExportStagingDirectory(string parentDirectory)
    {
        var parent = Path.GetFullPath(parentDirectory);
        var path = PathsEqual(parent, Path.GetTempPath())
            ? Path.Combine(parent, "PNCPKing", Guid.NewGuid().ToString("N"))
            : Path.Combine(parent, $".pncpking-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateImportStagingDirectory()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(repository.DatabasePath))!;
        var path = Path.Combine(directory, $".pncpking-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private void CleanupStaleDatabaseStagingDirectories(TimeSpan maximumAge)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(repository.DatabasePath))!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var threshold = DateTime.UtcNow - maximumAge;
        foreach (var path in Directory.EnumerateDirectories(
                     directory,
                     ".pncpking-import-*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(path) < threshold)
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Another process may still own this staging folder.
            }
        }
    }

    private string BuildRecoveryPath()
    {
        var databasePath = Path.GetFullPath(repository.DatabasePath);
        return $"{databasePath}.before-import-" +
               $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bak";
    }

    private bool IsRecoveryBackupPath(string candidatePath)
    {
        var databasePath = Path.GetFullPath(repository.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath)!;
        var candidate = Path.GetFullPath(candidatePath);
        if (!PathsEqual(Path.GetDirectoryName(candidate)!, directory))
        {
            return false;
        }

        var fileName = Path.GetFileName(candidate);
        var prefix = Path.GetFileName(databasePath) + ".before-import-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = fileName[prefix.Length..^4];
        return token.Length == 52 &&
               token[19] == '-' &&
               DateTime.TryParseExact(
                   token[..19],
                   "yyyyMMdd-HHmmss-fff",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _) &&
               Guid.TryParseExact(token[20..], "N", out _);
    }

    private static async Task<string> CopyFileWithHashAsync(
        string sourcePath,
        Stream destination,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ProgressBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var report = progress ?? ((_, _) => { });
        return await CopyWithProgressAsync(
                source,
                destination,
                source.Length,
                report,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        Action<long, long> progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ProgressBufferBytes];
        long processed = 0;
        var lastReported = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            processed += read;
            if (processed - lastReported >= 16L * ProgressBufferBytes || processed == totalBytes)
            {
                progress(processed, totalBytes);
                lastReported = processed;
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static double Scale(long value, long total, double minimum, double maximum)
    {
        if (total <= 0)
        {
            return minimum;
        }

        return minimum + Math.Clamp(value / (double)total, 0d, 1d) * (maximum - minimum);
    }

    private static void ReportExport(
        IProgress<BackupExportProgress>? progress,
        BackupExportStage stage,
        double percentage,
        string message,
        long bytesProcessed = 0,
        long totalBytes = 0,
        bool isIndeterminate = false,
        bool canCancel = true) =>
        progress?.Report(new BackupExportProgress(
            stage,
            Math.Clamp(percentage, 0d, 100d),
            message,
            bytesProcessed,
            totalBytes,
            isIndeterminate,
            canCancel));

    private static void Report(
        IProgress<BackupImportProgress>? progress,
        BackupImportStage stage,
        double percentage,
        string message,
        long bytesProcessed = 0,
        long totalBytes = 0,
        bool isIndeterminate = false,
        bool canCancel = true) =>
        progress?.Report(new BackupImportProgress(
            stage,
            Math.Clamp(percentage, 0d, 100d),
            message,
            bytesProcessed,
            totalBytes,
            isIndeterminate,
            canCancel));

    private static string NormalizeBackupPath(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var path = Path.GetFullPath(destinationPath);
        return path.EndsWith(".pncpking", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".pncpking";
    }

    private static string GetVolumeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.GetPathRoot(fullPath) ?? fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string GetDriveFormat(string volumeRoot)
    {
        try
        {
            return new DriveInfo(volumeRoot).DriveFormat;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DateOnly? ParseOptionalDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateOnly.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : null;
    }

    private static long AddSaturated(params long[] values)
    {
        long result = 0;
        foreach (var value in values)
        {
            if (value <= 0)
            {
                continue;
            }

            if (result > long.MaxValue - value)
            {
                return long.MaxValue;
            }

            result += value;
        }

        return result;
    }

    private static long GetAvailableBytes(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

    private sealed record EvidenceAssetRow(
        string Sha256,
        string RelativePath,
        long ByteLength);

    private sealed record SnapshotMetadata(
        DateOnly? StartDate,
        DateOnly? EndDate,
        string Scope,
        long Contracts,
        long Items,
        long Results,
        long PriceCacheContracts);

    private static void DeleteSidecar(string databasePath, string suffix)
    {
        var path = databasePath + suffix;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
