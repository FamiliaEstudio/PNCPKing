using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Infrastructure.Services;

public sealed class QuotationPackageService : IQuotationPackageService
{
    private const string PackageExtension = ".pncpcotacao";
    private const string PackageKind = "PNCPKing.QuotationPackage";
    private const int FormatVersion = 1;
    private const int MinimumCompatibleDatabaseSchemaVersion = 12;
    private const string ManifestEntryName = "manifest.json";
    private const string DataEntryName = "quotation.json";
    private const long MaximumManifestBytes = 2 * 1024 * 1024;
    private const long MaximumDataBytes = 256 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly TableDefinition[] TableDefinitions =
    [
        new(
            "quotation_projects",
            "SELECT p.* FROM quotation_projects p WHERE p.id = $projectId ORDER BY p.id;",
            ["id"]),
        new(
            "quotation_automation_runs",
            "SELECT r.* FROM quotation_automation_runs r WHERE r.project_id = $projectId ORDER BY r.created_at, r.id;",
            ["id"]),
        new(
            "quotation_lines",
            "SELECT l.* FROM quotation_lines l WHERE l.project_id = $projectId ORDER BY l.display_order, l.id;",
            ["id"]),
        new(
            "quotation_catalog_selections",
            """
            SELECT s.*
              FROM quotation_catalog_selections s
              JOIN quotation_lines l ON l.id = s.line_id
             WHERE l.project_id = $projectId
             ORDER BY s.line_id;
            """,
            ["line_id"]),
        new(
            "quotation_references",
            """
            SELECT r.*
              FROM quotation_references r
              JOIN quotation_lines l ON l.id = r.line_id
             WHERE l.project_id = $projectId
             ORDER BY r.line_id, r.id;
            """,
            ["line_id", "id"]),
        new(
            "quotation_manual_baskets",
            """
            SELECT b.*
              FROM quotation_manual_baskets b
              JOIN quotation_lines l ON l.id = b.line_id
             WHERE l.project_id = $projectId
             ORDER BY b.line_id, b.display_order, b.id;
            """,
            ["id"]),
        new(
            "quotation_manual_basket_references",
            """
            SELECT m.*
              FROM quotation_manual_basket_references m
              JOIN quotation_lines l ON l.id = m.line_id
             WHERE l.project_id = $projectId
             ORDER BY m.basket_id, m.display_order, m.reference_id;
            """,
            ["basket_id", "reference_id"]),
        new(
            "quotation_line_search_prompts",
            """
            SELECT p.*
              FROM quotation_line_search_prompts p
              JOIN quotation_lines l ON l.id = p.line_id
             WHERE l.project_id = $projectId
             ORDER BY p.line_id, p.version;
            """,
            ["line_id", "version"]),
        new(
            "quotation_contract_search_prompts",
            """
            SELECT p.*
              FROM quotation_contract_search_prompts p
              JOIN quotation_automation_runs r ON r.id = p.run_id
             WHERE r.project_id = $projectId
             ORDER BY p.run_id, p.display_order;
            """,
            ["run_id", "display_order"]),
        new(
            "quotation_processed_contracts",
            """
            SELECT p.*
              FROM quotation_processed_contracts p
              JOIN quotation_automation_runs r ON r.id = p.run_id
             WHERE r.project_id = $projectId
             ORDER BY p.run_id, p.processed_at, p.contract_id;
            """,
            ["run_id", "contract_id"],
            SkipsMissingContracts: true),
        new(
            "quotation_prompt_revalidations",
            """
            SELECT p.*
              FROM quotation_prompt_revalidations p
              JOIN quotation_automation_runs r ON r.id = p.run_id
             WHERE r.project_id = $projectId
             ORDER BY p.run_id, p.line_id, p.prompt_version;
            """,
            ["run_id", "line_id", "prompt_version"]),
        new(
            "quotation_internet_evidence_assets",
            """
            SELECT a.*
              FROM quotation_internet_evidence_assets a
             WHERE a.sha256 IN (
                    SELECT d.price_image_sha256
                      FROM quotation_internet_price_drafts d
                      JOIN quotation_lines l ON l.id = d.line_id
                     WHERE l.project_id = $projectId AND d.price_image_sha256 IS NOT NULL
                    UNION
                    SELECT d.tax_id_image_sha256
                      FROM quotation_internet_price_drafts d
                      JOIN quotation_lines l ON l.id = d.line_id
                     WHERE l.project_id = $projectId AND d.tax_id_image_sha256 IS NOT NULL
                    UNION
                    SELECT e.price_image_sha256
                      FROM quotation_internet_price_evidence e
                      JOIN quotation_lines l ON l.id = e.line_id
                     WHERE l.project_id = $projectId
                    UNION
                    SELECT e.tax_id_image_sha256
                      FROM quotation_internet_price_evidence e
                      JOIN quotation_lines l ON l.id = e.line_id
                     WHERE l.project_id = $projectId)
             ORDER BY a.sha256;
            """,
            ["sha256"],
            UpsertsExistingRows: true),
        new(
            "quotation_internet_price_drafts",
            """
            SELECT d.*
              FROM quotation_internet_price_drafts d
              JOIN quotation_lines l ON l.id = d.line_id
             WHERE l.project_id = $projectId
             ORDER BY d.line_id, d.created_at, d.id;
            """,
            ["id"]),
        new(
            "quotation_internet_price_evidence",
            """
            SELECT e.*
              FROM quotation_internet_price_evidence e
              JOIN quotation_lines l ON l.id = e.line_id
             WHERE l.project_id = $projectId
             ORDER BY e.line_id, e.reference_id;
            """,
            ["line_id", "reference_id"]),
        new(
            "quotation_item_search_workspaces",
            """
            SELECT w.*
              FROM quotation_item_search_workspaces w
              JOIN quotation_lines l ON l.id = w.line_id
             WHERE l.project_id = $projectId
             ORDER BY w.line_id, w.prompt_slot;
            """,
            ["line_id", "prompt_slot"]),
        new(
            "quotation_item_search_hits",
            """
            SELECT h.*
              FROM quotation_item_search_hits h
              JOIN quotation_lines l ON l.id = h.line_id
             WHERE l.project_id = $projectId
             ORDER BY h.line_id, h.prompt_slot, h.discovered_order, h.contract_id, h.item_number;
            """,
            ["line_id", "prompt_slot", "contract_id", "item_number"])
    ];

    private readonly string _databasePath;
    private readonly string _dataFolder;
    private readonly string _connectionString;
    private readonly Action? _beforeImportCommit;

    public QuotationPackageService(string databasePath, string dataFolder)
        : this(databasePath, dataFolder, beforeImportCommit: null)
    {
    }

    internal QuotationPackageService(
        string databasePath,
        string dataFolder,
        Action? beforeImportCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _databasePath = Path.GetFullPath(databasePath);
        _dataFolder = Path.GetFullPath(dataFolder);
        _beforeImportCommit = beforeImportCommit;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public async Task ExportAsync(
        string destinationPath,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var normalizedDestination = EnsureExtension(destinationPath);
        var temporaryArchive = normalizedDestination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(normalizedDestination))!);
        if (File.Exists(temporaryArchive))
        {
            File.Delete(temporaryArchive);
        }

        try
        {
            var (payload, projectName) = await ReadProjectPayloadAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
            await ValidatePayloadAsync(payload, null, cancellationToken).ConfigureAwait(false);
            var payloadBytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions));
            var payloadHash = ComputeSha256(payloadBytes);
            var assetRows = GetRows(payload, "quotation_internet_evidence_assets");
            var assets = new List<QuotationPackageAssetManifest>(assetRows.Count);
            foreach (var row in assetRows)
            {
                var sha256 = NormalizeHash(GetRequiredText(row, "sha256"));
                var byteLength = GetRequiredLong(row, "byte_length");
                var sourcePath = ResolveEvidencePath(GetRequiredText(row, "relative_path"));
                await ValidateEvidenceFileAsync(sourcePath, sha256, byteLength, cancellationToken)
                    .ConfigureAwait(false);
                assets.Add(new QuotationPackageAssetManifest
                {
                    Sha256 = sha256,
                    ArchivePath = EvidenceArchivePath(sha256),
                    ByteLength = byteLength
                });
            }

            var manifest = BuildManifest(payload, projectId, projectName, payloadHash, assets);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await using (var output = new FileStream(
                             temporaryArchive,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             131_072,
                             FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteEntryAsync(
                    archive,
                    ManifestEntryName,
                    manifestBytes,
                    CompressionLevel.Optimal,
                    cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(
                    archive,
                    DataEntryName,
                    payloadBytes,
                    CompressionLevel.Optimal,
                    cancellationToken).ConfigureAwait(false);
                foreach (var asset in assets)
                {
                    var row = assetRows.Single(value =>
                        string.Equals(
                            GetRequiredText(value, "sha256"),
                            asset.Sha256,
                            StringComparison.OrdinalIgnoreCase));
                    var entry = archive.CreateEntry(asset.ArchivePath, CompressionLevel.Fastest);
                    await using var destination = entry.Open();
                    await using var source = new FileStream(
                        ResolveEvidencePath(GetRequiredText(row, "relative_path")),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        131_072,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporaryArchive, normalizedDestination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }
        }
    }

    public async Task<QuotationPackagePreview> InspectAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var package = await LoadAndValidatePackageAsync(
                sourcePath,
                evidenceStagingFolder: null,
                cancellationToken)
            .ConfigureAwait(false);
        return new QuotationPackagePreview
        {
            ProjectId = package.Manifest.ProjectId,
            ProjectName = package.Manifest.ProjectName,
            ExportedAt = package.Manifest.ExportedAt,
            ItemCount = package.Manifest.ItemCount,
            ReferenceCount = package.Manifest.ReferenceCount,
            ManualBasketCount = package.Manifest.ManualBasketCount,
            EvidenceCount = package.Manifest.EvidenceAssets.Count,
            HasProjectConflict = await ProjectExistsAsync(
                    package.Manifest.ProjectId,
                    cancellationToken)
                .ConfigureAwait(false),
            HasIncompleteAutomation = package.Manifest.HasIncompleteAutomation
        };
    }

    public async Task<QuotationPackageImportResult> ImportAsync(
        string sourcePath,
        QuotationPackageImportMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var temporaryDirectory = CreateTemporaryDirectory();
        var stagingFolder = Path.Combine(temporaryDirectory, "internet-evidence");
        var createdEvidencePaths = new List<string>();
        try
        {
            var package = await LoadAndValidatePackageAsync(
                    sourcePath,
                    stagingFolder,
                    cancellationToken)
                .ConfigureAwait(false);
            var conflict = await ProjectExistsAsync(package.Manifest.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            if (mode == QuotationPackageImportMode.PreserveIdentity && conflict)
            {
                throw new InvalidOperationException(
                    "Esta cotação já existe. Escolha importar como cópia ou substituir.");
            }

            if (mode == QuotationPackageImportMode.Replace && !conflict)
            {
                throw new InvalidOperationException(
                    "A cotação que seria substituída não existe mais.");
            }

            var payload = (JsonObject)package.Payload.DeepClone();
            UpgradeCompatiblePayload(
                payload,
                package.Manifest.DatabaseSchemaVersion);
            var warnings = new List<string>();
            var projectId = package.Manifest.ProjectId;
            var projectName = package.Manifest.ProjectName;
            if (mode == QuotationPackageImportMode.Copy)
            {
                (projectId, projectName) = await RemapAsCopyAsync(
                        payload,
                        package.Manifest.ProjectName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            NormalizePortableAutomation(payload, warnings);
            await ValidatePayloadAsync(payload, null, cancellationToken).ConfigureAwait(false);

            string? recoveryPackagePath = null;
            if (mode == QuotationPackageImportMode.Replace)
            {
                recoveryPackagePath = BuildRecoveryPackagePath(package.Manifest.ProjectName);
                await ExportAsync(recoveryPackagePath, package.Manifest.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (mode == QuotationPackageImportMode.Replace)
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = (SqliteTransaction)transaction;
                    delete.CommandText = "DELETE FROM quotation_projects WHERE id = $id;";
                    delete.Parameters.AddWithValue("$id", package.Manifest.ProjectId.ToString("N"));
                    if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        throw new InvalidOperationException(
                            "A cotação que seria substituída não existe mais.");
                    }
                }

                var skippedContracts = await InsertPayloadAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (skippedContracts > 0)
                {
                    warnings.Add(
                        $"{skippedContracts:N0} marcador(es) de contratos processados não foram restaurados " +
                        "porque esses contratos ainda não existem no índice PNCP deste computador.");
                }

                await InstallEvidenceAsync(
                        package.Manifest.EvidenceAssets,
                        stagingFolder,
                        createdEvidencePaths,
                        cancellationToken)
                    .ConfigureAwait(false);
                _beforeImportCommit?.Invoke();
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var createdPath in createdEvidencePaths)
                {
                    try
                    {
                        if (File.Exists(createdPath))
                        {
                            File.Delete(createdPath);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                throw;
            }

            return new QuotationPackageImportResult
            {
                ProjectId = projectId,
                ProjectName = projectName,
                ImportedAsCopy = mode == QuotationPackageImportMode.Copy,
                RecoveryPackagePath = recoveryPackagePath,
                Warnings = warnings
            };
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private async Task<(JsonObject Payload, string ProjectName)> ReadProjectPayloadAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var tables = new JsonObject();
        foreach (var definition in TableDefinitions)
        {
            tables[definition.Name] = await ReadRowsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    definition.ExportQuery,
                    projectId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var projectRows = GetRowsFromTables(tables, "quotation_projects");
        if (projectRows.Count != 1)
        {
            throw new InvalidOperationException("A cotação selecionada não existe mais.");
        }

        var projectName = GetRequiredText(projectRows[0], "name");
        var payload = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["basketAlgorithmVersion"] = QuotationAnalyzer.BasketAlgorithmVersion,
            ["projectId"] = projectId.ToString("N"),
            ["tables"] = tables
        };
        return (payload, projectName);
    }

    private QuotationPackageManifest BuildManifest(
        JsonObject payload,
        Guid projectId,
        string projectName,
        string payloadHash,
        IReadOnlyList<QuotationPackageAssetManifest> assets)
    {
        var runs = GetRows(payload, "quotation_automation_runs");
        return new QuotationPackageManifest
        {
            Kind = PackageKind,
            FormatVersion = FormatVersion,
            BasketAlgorithmVersion = QuotationAnalyzer.BasketAlgorithmVersion,
            DatabaseSchemaVersion = SqliteContractRepository.CurrentSchemaVersion,
            AppVersion = typeof(QuotationPackageService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            ExportedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            ProjectName = projectName,
            ItemCount = GetRows(payload, "quotation_lines").Count,
            ReferenceCount = GetRows(payload, "quotation_references").Count,
            ManualBasketCount = GetRows(payload, "quotation_manual_baskets").Count,
            HasIncompleteAutomation = runs.Any(row =>
                GetRequiredLong(row, "state") != (long)QuotationAutomationRunState.Completed),
            DataSha256 = payloadHash,
            EvidenceAssets = assets
        };
    }

    private async Task<LoadedPackage> LoadAndValidatePackageAsync(
        string sourcePath,
        string? evidenceStagingFolder,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("O pacote de cotação não foi encontrado.", sourcePath);
        }

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.ToArray();
        if (entries
            .GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("O pacote contém entradas duplicadas.");
        }

        var manifestEntry = entries.SingleOrDefault(entry =>
                                string.Equals(
                                    entry.FullName,
                                    ManifestEntryName,
                                    StringComparison.Ordinal))
                            ?? throw new InvalidDataException(
                                $"O pacote não contém {ManifestEntryName}.");
        var dataEntry = entries.SingleOrDefault(entry =>
                            string.Equals(entry.FullName, DataEntryName, StringComparison.Ordinal))
                        ?? throw new InvalidDataException(
                            $"O pacote não contém {DataEntryName}.");
        var manifestBytes = await ReadEntryBytesAsync(
                manifestEntry,
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<QuotationPackageManifest>(
                           manifestBytes,
                           JsonOptions)
                       ?? throw new InvalidDataException("O manifesto do pacote é inválido.");
        ValidateManifest(manifest);

        var expectedEntries = new HashSet<string>(
            manifest.EvidenceAssets.Select(asset => asset.ArchivePath),
            StringComparer.Ordinal)
        {
            ManifestEntryName,
            DataEntryName
        };
        if (entries.Any(entry => !expectedEntries.Contains(entry.FullName)) ||
            expectedEntries.Any(name => entries.All(entry =>
                !string.Equals(entry.FullName, name, StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                "O conteúdo do pacote não corresponde às entradas declaradas no manifesto.");
        }

        var dataBytes = await ReadEntryBytesAsync(dataEntry, MaximumDataBytes, cancellationToken)
            .ConfigureAwait(false);
        var actualDataHash = ComputeSha256(dataBytes);
        if (!FixedTimeEqualsHex(actualDataHash, manifest.DataSha256))
        {
            throw new InvalidDataException(
                $"O checksum de {DataEntryName} não corresponde ao manifesto.");
        }

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(dataBytes) as JsonObject
                      ?? throw new InvalidDataException(
                          $"{DataEntryName} não contém um objeto JSON.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{DataEntryName} é inválido.", exception);
        }

        await ValidatePayloadAsync(payload, manifest, cancellationToken).ConfigureAwait(false);
        if (evidenceStagingFolder is not null)
        {
            Directory.CreateDirectory(evidenceStagingFolder);
        }

        foreach (var asset in manifest.EvidenceAssets)
        {
            var entry = archive.GetEntry(asset.ArchivePath)
                        ?? throw new InvalidDataException(
                            $"O pacote não contém a evidência {asset.Sha256}.");
            var stagedPath = evidenceStagingFolder is null
                ? null
                : Path.Combine(evidenceStagingFolder, $"{asset.Sha256}.png");
            var (hash, length) = await HashArchiveEntryAsync(
                    entry,
                    stagedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (length != asset.ByteLength || !FixedTimeEqualsHex(hash, asset.Sha256))
            {
                throw new InvalidDataException(
                    $"A evidência {asset.Sha256} não corresponde ao manifesto.");
            }
        }

        return new LoadedPackage(manifest, payload);
    }

    private async Task ValidatePayloadAsync(
        JsonObject payload,
        QuotationPackageManifest? manifest,
        CancellationToken cancellationToken)
    {
        if (GetRequiredLong(payload, "formatVersion") != FormatVersion ||
            GetRequiredLong(payload, "basketAlgorithmVersion") !=
            QuotationAnalyzer.BasketAlgorithmVersion)
        {
            throw new InvalidDataException(
                "A versão interna do pacote de cotação é incompatível.");
        }

        var tables = payload["tables"] as JsonObject
                     ?? throw new InvalidDataException(
                         "O pacote não contém as tabelas da cotação.");
        var compatibleDefinitions = manifest is { DatabaseSchemaVersion: < 14 }
            ? TableDefinitions.Where(definition => !string.Equals(
                definition.Name,
                "quotation_catalog_selections",
                StringComparison.Ordinal)).ToArray()
            : TableDefinitions;
        var expectedNames = compatibleDefinitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (tables.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames) == false)
        {
            throw new InvalidDataException(
                "O pacote contém um conjunto de tabelas incompatível.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var definition in compatibleDefinitions)
        {
            var columns = await ReadTableColumnsAsync(
                    connection,
                    definition.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            var compatibleColumns = columns.ToHashSet(StringComparer.Ordinal);
            if (manifest?.DatabaseSchemaVersion == 12 &&
                string.Equals(
                    definition.Name,
                    "quotation_references",
                    StringComparison.Ordinal))
            {
                compatibleColumns.Remove("supplier_municipality");
                compatibleColumns.Remove("supplier_uf");
            }

            if (manifest?.DatabaseSchemaVersion < 14 &&
                string.Equals(definition.Name, "quotation_lines", StringComparison.Ordinal))
            {
                compatibleColumns.Remove("display_name");
            }

            var rows = GetRowsFromTables(tables, definition.Name);
            foreach (var row in rows)
            {
                if (!row.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(compatibleColumns))
                {
                    throw new InvalidDataException(
                        $"A estrutura da tabela {definition.Name} é incompatível.");
                }
            }

            EnsureUniqueRows(rows, definition.Name, definition.KeyColumns);
        }

        ValidateRelationships(payload);
        ValidateSelectedBaskets(payload);
        var project = GetRows(payload, "quotation_projects").Single();
        var projectId = ParseGuid(GetRequiredText(project, "id"), "projeto");
        var payloadProjectId = ParseGuid(
            GetRequiredText(payload, "projectId"),
            "identificador do pacote");
        if (projectId != payloadProjectId)
        {
            throw new InvalidDataException(
                "O identificador do projeto não corresponde aos dados do pacote.");
        }

        if (manifest is null)
        {
            return;
        }

        if (manifest.ProjectId != projectId ||
            !string.Equals(
                manifest.ProjectName,
                GetRequiredText(project, "name"),
                StringComparison.Ordinal) ||
            manifest.ItemCount != GetRows(payload, "quotation_lines").Count ||
            manifest.ReferenceCount != GetRows(payload, "quotation_references").Count ||
            manifest.ManualBasketCount != GetRows(payload, "quotation_manual_baskets").Count)
        {
            throw new InvalidDataException(
                "As contagens ou o projeto do manifesto não correspondem aos dados.");
        }

        var assetRows = GetRows(payload, "quotation_internet_evidence_assets")
            .ToDictionary(
                row => NormalizeHash(GetRequiredText(row, "sha256")),
                StringComparer.OrdinalIgnoreCase);
        if (assetRows.Count != manifest.EvidenceAssets.Count)
        {
            throw new InvalidDataException(
                "A quantidade de evidências não corresponde ao manifesto.");
        }

        foreach (var asset in manifest.EvidenceAssets)
        {
            if (!assetRows.TryGetValue(asset.Sha256, out var row) ||
                GetRequiredLong(row, "byte_length") != asset.ByteLength ||
                !string.Equals(
                    GetRequiredText(row, "relative_path").Replace('\\', '/'),
                    EvidenceArchivePath(asset.Sha256),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"A evidência {asset.Sha256} não corresponde aos dados da cotação.");
            }
        }
    }

    private static void ValidateRelationships(JsonObject payload)
    {
        var projects = GetRows(payload, "quotation_projects");
        if (projects.Count != 1)
        {
            throw new InvalidDataException(
                "O pacote deve conter exatamente um projeto de cotação.");
        }

        var projectId = GetRequiredText(projects[0], "id");
        var runs = GetRows(payload, "quotation_automation_runs");
        var runIds = runs.Select(row => GetRequiredText(row, "id"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            EnsureGuid(run, "id", "automação");
            EnsureSame(GetRequiredText(run, "project_id"), projectId, "automação/projeto");
        }

        var lines = GetRows(payload, "quotation_lines");
        var lineIds = lines.Select(row => GetRequiredText(row, "id"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            EnsureGuid(line, "id", "item");
            EnsureSame(GetRequiredText(line, "project_id"), projectId, "item/projeto");
            var runId = GetOptionalText(line, "automation_run_id");
            if (runId is not null && !runIds.Contains(runId))
            {
                throw new InvalidDataException(
                    "Um item referencia uma automação ausente do pacote.");
            }
        }

        var references = GetRows(payload, "quotation_references");
        var referenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var lineId = GetRequiredText(reference, "line_id");
            EnsureContains(lineIds, lineId, "Uma referência aponta para um item ausente.");
            referenceKeys.Add(Composite(lineId, GetRequiredText(reference, "id")));
        }

        foreach (var selection in GetOptionalRows(payload, "quotation_catalog_selections"))
        {
            EnsureContains(
                lineIds,
                GetRequiredText(selection, "line_id"),
                "Uma seleção CATMAT/CATSER aponta para um item ausente.");
            var kind = GetRequiredLong(selection, "catalog_kind");
            if (kind is < 1 or > 2 ||
                string.IsNullOrWhiteSpace(GetRequiredText(selection, "catalog_code")) ||
                string.IsNullOrWhiteSpace(GetRequiredText(selection, "description_snapshot")))
            {
                throw new InvalidDataException("Uma seleção CATMAT/CATSER é inválida.");
            }
        }

        var baskets = GetRows(payload, "quotation_manual_baskets");
        var basketLines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var basket in baskets)
        {
            var basketId = GetRequiredText(basket, "id");
            EnsureGuid(basket, "id", "cesta manual");
            var lineId = GetRequiredText(basket, "line_id");
            EnsureContains(lineIds, lineId, "Uma cesta aponta para um item ausente.");
            basketLines.Add(basketId, lineId);
        }

        foreach (var member in GetRows(payload, "quotation_manual_basket_references"))
        {
            var basketId = GetRequiredText(member, "basket_id");
            var lineId = GetRequiredText(member, "line_id");
            if (!basketLines.TryGetValue(basketId, out var basketLine) ||
                !string.Equals(basketLine, lineId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Um membro aponta para uma cesta manual incompatível.");
            }

            EnsureContains(
                referenceKeys,
                Composite(lineId, GetRequiredText(member, "reference_id")),
                "Uma cesta manual aponta para uma referência ausente.");
        }

        foreach (var prompt in GetRows(payload, "quotation_line_search_prompts"))
        {
            EnsureContains(
                lineIds,
                GetRequiredText(prompt, "line_id"),
                "Um prompt aponta para um item ausente.");
        }

        foreach (var prompt in GetRows(payload, "quotation_contract_search_prompts"))
        {
            EnsureContains(
                runIds,
                GetRequiredText(prompt, "run_id"),
                "Um prompt global aponta para uma automação ausente.");
        }

        foreach (var processed in GetRows(payload, "quotation_processed_contracts"))
        {
            EnsureContains(
                runIds,
                GetRequiredText(processed, "run_id"),
                "Um contrato processado aponta para uma automação ausente.");
        }

        foreach (var revalidation in GetRows(payload, "quotation_prompt_revalidations"))
        {
            EnsureContains(
                runIds,
                GetRequiredText(revalidation, "run_id"),
                "Uma revalidação aponta para uma automação ausente.");
            EnsureContains(
                lineIds,
                GetRequiredText(revalidation, "line_id"),
                "Uma revalidação aponta para um item ausente.");
        }

        var assets = GetRows(payload, "quotation_internet_evidence_assets")
            .Select(row => NormalizeHash(GetRequiredText(row, "sha256")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in GetRows(payload, "quotation_internet_price_drafts"))
        {
            var lineId = GetRequiredText(draft, "line_id");
            EnsureContains(lineIds, lineId, "Um rascunho web aponta para um item ausente.");
            var basketId = GetOptionalText(draft, "basket_id");
            if (basketId is not null &&
                (!basketLines.TryGetValue(basketId, out var basketLine) ||
                 !string.Equals(basketLine, lineId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Um rascunho web aponta para uma cesta incompatível.");
            }

            EnsureOptionalAsset(draft, "price_image_sha256", assets);
            EnsureOptionalAsset(draft, "tax_id_image_sha256", assets);
        }

        foreach (var evidence in GetRows(payload, "quotation_internet_price_evidence"))
        {
            var lineId = GetRequiredText(evidence, "line_id");
            EnsureContains(
                referenceKeys,
                Composite(lineId, GetRequiredText(evidence, "reference_id")),
                "Uma evidência web aponta para uma referência ausente.");
            EnsureAsset(evidence, "price_image_sha256", assets);
            EnsureAsset(evidence, "tax_id_image_sha256", assets);
        }

        var workspaceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workspace in GetRows(payload, "quotation_item_search_workspaces"))
        {
            var lineId = GetRequiredText(workspace, "line_id");
            EnsureContains(lineIds, lineId, "Uma pesquisa aponta para um item ausente.");
            workspaceKeys.Add(Composite(
                lineId,
                GetRequiredLong(workspace, "prompt_slot").ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var hit in GetRows(payload, "quotation_item_search_hits"))
        {
            EnsureContains(
                workspaceKeys,
                Composite(
                    GetRequiredText(hit, "line_id"),
                    GetRequiredLong(hit, "prompt_slot").ToString(CultureInfo.InvariantCulture)),
                "Um resultado de pesquisa aponta para uma área ausente.");
        }
    }

    private static void ValidateSelectedBaskets(JsonObject payload)
    {
        var references = GetRows(payload, "quotation_references")
            .GroupBy(row => GetRequiredText(row, "line_id"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<QuotationReference>)group.Select(MapReference).ToArray(),
                StringComparer.Ordinal);
        var members = GetRows(payload, "quotation_manual_basket_references")
            .GroupBy(row => GetRequiredText(row, "basket_id"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(row => GetRequiredLong(row, "display_order"))
                    .Select(row => GetRequiredText(row, "reference_id"))
                    .ToArray(),
                StringComparer.Ordinal);
        var baskets = GetRows(payload, "quotation_manual_baskets")
            .GroupBy(row => GetRequiredText(row, "line_id"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<QuotationManualBasket>)group.Select(row =>
                {
                    var id = ParseGuid(GetRequiredText(row, "id"), "cesta manual");
                    return new QuotationManualBasket
                    {
                        Id = id,
                        LineId = ParseGuid(GetRequiredText(row, "line_id"), "item da cesta"),
                        Name = GetRequiredText(row, "name"),
                        DisplayOrder = checked((int)GetRequiredLong(row, "display_order")),
                        CreatedAt = ParseDateTime(GetRequiredText(row, "created_at")),
                        UpdatedAt = ParseDateTime(GetRequiredText(row, "updated_at")),
                        ReferenceIds = members.GetValueOrDefault(id.ToString("N"), [])
                    };
                }).ToArray(),
                StringComparer.Ordinal);
        var analyzer = new QuotationAnalyzer();
        foreach (var row in GetRows(payload, "quotation_lines"))
        {
            var line = MapLine(row);
            if (!line.SelectionConfirmed || string.IsNullOrWhiteSpace(line.SelectedBasketKey))
            {
                continue;
            }

            var lineKey = line.Id.ToString("N");
            var analysis = analyzer.Analyze(
                line,
                references.GetValueOrDefault(lineKey, []),
                baskets.GetValueOrDefault(lineKey, []));
            if (analysis.Baskets.All(basket =>
                    !string.Equals(
                        basket.Key,
                        line.SelectedBasketKey,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"A cesta confirmada do item '{line.Description}' não pode ser recriada " +
                    "pela versão atual do cálculo.");
            }
        }
    }

    private async Task<(Guid ProjectId, string ProjectName)> RemapAsCopyAsync(
        JsonObject payload,
        string originalName,
        CancellationToken cancellationToken)
    {
        var projectMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GetRequiredText(
                GetRows(payload, "quotation_projects").Single(),
                "id")] = Guid.NewGuid().ToString("N")
        };
        var runMap = CreateGuidMap(GetRows(payload, "quotation_automation_runs"), "id");
        var lineMap = CreateGuidMap(GetRows(payload, "quotation_lines"), "id");
        var basketMap = CreateGuidMap(GetRows(payload, "quotation_manual_baskets"), "id");
        var draftMap = CreateGuidMap(GetRows(payload, "quotation_internet_price_drafts"), "id");

        Remap(GetRows(payload, "quotation_projects"), "id", projectMap);
        Remap(GetRows(payload, "quotation_automation_runs"), "id", runMap);
        Remap(GetRows(payload, "quotation_automation_runs"), "project_id", projectMap);
        Remap(GetRows(payload, "quotation_lines"), "id", lineMap);
        Remap(GetRows(payload, "quotation_lines"), "project_id", projectMap);
        RemapOptional(GetRows(payload, "quotation_lines"), "automation_run_id", runMap);
        Remap(GetRows(payload, "quotation_catalog_selections"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_references"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_manual_baskets"), "id", basketMap);
        Remap(GetRows(payload, "quotation_manual_baskets"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_manual_basket_references"), "basket_id", basketMap);
        Remap(GetRows(payload, "quotation_manual_basket_references"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_line_search_prompts"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_contract_search_prompts"), "run_id", runMap);
        Remap(GetRows(payload, "quotation_processed_contracts"), "run_id", runMap);
        Remap(GetRows(payload, "quotation_prompt_revalidations"), "run_id", runMap);
        Remap(GetRows(payload, "quotation_prompt_revalidations"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_internet_price_drafts"), "id", draftMap);
        Remap(GetRows(payload, "quotation_internet_price_drafts"), "line_id", lineMap);
        RemapOptional(GetRows(payload, "quotation_internet_price_drafts"), "basket_id", basketMap);
        Remap(GetRows(payload, "quotation_internet_price_evidence"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_item_search_workspaces"), "line_id", lineMap);
        Remap(GetRows(payload, "quotation_item_search_hits"), "line_id", lineMap);

        foreach (var line in GetRows(payload, "quotation_lines"))
        {
            var selected = GetOptionalText(line, "selected_basket_key");
            if (selected is null ||
                !selected.StartsWith("manual:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var oldId = selected["manual:".Length..];
            if (!basketMap.TryGetValue(oldId, out var replacement))
            {
                throw new InvalidDataException(
                    "A cesta manual selecionada não existe no pacote.");
            }

            line["selected_basket_key"] = $"manual:{replacement}";
        }

        var newProjectId = Guid.ParseExact(projectMap.Single().Value, "N");
        var projectName = await ResolveCopyNameAsync(originalName, cancellationToken)
            .ConfigureAwait(false);
        var project = GetRows(payload, "quotation_projects").Single();
        project["name"] = projectName;
        project["created_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        project["updated_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        payload["projectId"] = newProjectId.ToString("N");
        return (newProjectId, projectName);
    }

    private static void NormalizePortableAutomation(
        JsonObject payload,
        ICollection<string> warnings)
    {
        var clearedPaths = 0;
        var recoveredRuns = 0;
        foreach (var run in GetRows(payload, "quotation_automation_runs"))
        {
            if (!string.IsNullOrWhiteSpace(GetRequiredText(run, "output_path")))
            {
                run["output_path"] = string.Empty;
                clearedPaths++;
            }

            if (GetRequiredLong(run, "state") == (long)QuotationAutomationRunState.Running)
            {
                run["state"] = (long)QuotationAutomationRunState.Pending;
                run["message"] = "Execução importada; pronta para retomar.";
                recoveredRuns++;
            }
        }

        foreach (var line in GetRows(payload, "quotation_lines"))
        {
            if (GetRequiredLong(line, "automation_state") !=
                (long)QuotationAutomationItemState.Running)
            {
                continue;
            }

            line["automation_state"] = (long)QuotationAutomationItemState.Pending;
            line["automation_message"] = "Execução importada; pronta para retomar.";
        }

        if (clearedPaths > 0)
        {
            warnings.Add(
                "Caminhos de saída do computador de origem foram removidos; " +
                "ao retomar uma automação fixa, escolha um novo arquivo Excel.");
        }

        if (recoveredRuns > 0)
        {
            warnings.Add(
                $"{recoveredRuns:N0} automação(ões) em execução foram importadas como pendentes.");
        }
    }

    private static void UpgradeCompatiblePayload(
        JsonObject payload,
        int databaseSchemaVersion)
    {
        if (databaseSchemaVersion < 13)
        {
            foreach (var reference in GetRows(payload, "quotation_references"))
            {
                reference["supplier_municipality"] = string.Empty;
                reference["supplier_uf"] = string.Empty;
            }
        }

        if (databaseSchemaVersion < 14)
        {
            foreach (var line in GetRows(payload, "quotation_lines"))
            {
                line["display_name"] = GetRequiredText(line, "description");
            }

            var tables = payload["tables"] as JsonObject
                         ?? throw new InvalidDataException("O pacote não contém tabelas.");
            tables["quotation_catalog_selections"] = new JsonArray();
        }
    }

    private async Task<int> InsertPayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var skippedContracts = 0;
        foreach (var definition in TableDefinitions)
        {
            if (string.Equals(
                    definition.Name,
                    "quotation_line_search_prompts",
                    StringComparison.Ordinal))
            {
                await DeleteGeneratedPromptRowsAsync(
                        connection,
                        transaction,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var row in GetRows(payload, definition.Name))
            {
                if (definition.SkipsMissingContracts &&
                    !await ContractExistsAsync(
                            connection,
                            transaction,
                            GetRequiredText(row, "contract_id"),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    skippedContracts++;
                    continue;
                }

                await InsertRowAsync(
                        connection,
                        transaction,
                        definition,
                        row,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return skippedContracts;
    }

    private static async Task DeleteGeneratedPromptRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM quotation_line_search_prompts
             WHERE line_id IN (
                    SELECT id
                      FROM quotation_lines
                     WHERE project_id = $projectId);
            """;
        command.Parameters.AddWithValue(
            "$projectId",
            GetRequiredText(payload, "projectId"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableDefinition definition,
        JsonObject row,
        CancellationToken cancellationToken)
    {
        var columns = row.Select(pair => pair.Key).ToArray();
        var parameters = columns.Select((_, index) => $"$p{index}").ToArray();
        var conflictClause = definition.UpsertsExistingRows
            ? " ON CONFLICT(\"sha256\") DO UPDATE SET " +
              string.Join(
                  ", ",
                  columns
                      .Where(column => !string.Equals(
                          column,
                          "sha256",
                          StringComparison.Ordinal))
                      .Select(column => $"\"{column}\" = excluded.\"{column}\""))
            : string.Empty;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO \"{definition.Name}\" " +
            $"({string.Join(", ", columns.Select(column => $"\"{column}\""))}) " +
            $"VALUES({string.Join(", ", parameters)}){conflictClause};";
        for (var index = 0; index < columns.Length; index++)
        {
            command.Parameters.AddWithValue(
                parameters[index],
                ToDatabaseValue(row[columns[index]]));
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallEvidenceAsync(
        IReadOnlyList<QuotationPackageAssetManifest> assets,
        string stagingFolder,
        ICollection<string> createdPaths,
        CancellationToken cancellationToken)
    {
        if (assets.Count == 0)
        {
            return;
        }

        var destinationFolder = Path.Combine(_dataFolder, "internet-evidence");
        Directory.CreateDirectory(destinationFolder);
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(stagingFolder, $"{asset.Sha256}.png");
            var destination = Path.Combine(destinationFolder, $"{asset.Sha256}.png");
            var existed = File.Exists(destination);
            if (existed)
            {
                try
                {
                    await ValidateEvidenceFileAsync(
                            destination,
                            asset.Sha256,
                            asset.ByteLength,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                catch (InvalidDataException)
                {
                }
            }

            var partial = destination + $".{Guid.NewGuid():N}.partial";
            try
            {
                File.Copy(source, partial, overwrite: false);
                File.Move(partial, destination, overwrite: true);
                if (!existed)
                {
                    createdPaths.Add(destination);
                }
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

    private async Task<string> ResolveCopyNameAsync(
        string originalName,
        CancellationToken cancellationToken)
    {
        var basis = $"{originalName} (cópia importada)";
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM quotation_projects;";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        if (!names.Contains(basis))
        {
            return basis;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{basis} {number:N0}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task<bool> ProjectExistsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM quotation_projects WHERE id = $id);";
        command.Parameters.AddWithValue("$id", projectId.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private string BuildRecoveryPackagePath(string projectName)
    {
        var folder = Path.Combine(_dataFolder, "quotation-import-recovery");
        Directory.CreateDirectory(folder);
        var safeName = SanitizeFileName(projectName);
        var timestamp = DateTime.Now.ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        return Path.Combine(folder, $"{safeName}-antes-importacao-{timestamp}{PackageExtension}");
    }

    private string ResolveEvidencePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                "O caminho de uma evidência não pode ser absoluto.");
        }

        var evidenceRoot = Path.GetFullPath(Path.Combine(_dataFolder, "internet-evidence"));
        var fullPath = Path.GetFullPath(
            Path.Combine(_dataFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = evidenceRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "O caminho de uma evidência saiu da pasta permitida.");
        }

        return fullPath;
    }

    private static async Task<JsonArray> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
        var rows = new JsonArray();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new JsonObject();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index)
                    ? null
                    : reader.GetValue(index) switch
                    {
                        long value => JsonValue.Create(value),
                        int value => JsonValue.Create((long)value),
                        double value => JsonValue.Create(value),
                        float value => JsonValue.Create((double)value),
                        string value => JsonValue.Create(value),
                        _ => throw new InvalidDataException(
                            $"A coluna {reader.GetName(index)} usa um tipo SQLite não suportado.")
                    };
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<IReadOnlySet<string>> ReadTableColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        if (columns.Count == 0)
        {
            throw new InvalidDataException(
                $"A tabela necessária {table} não existe no banco atual.");
        }

        return columns;
    }

    private static async Task<bool> ContractExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contractId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM contracts WHERE pncp_id = $id);";
        command.Parameters.AddWithValue("$id", contractId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        byte[] bytes,
        CompressionLevel compression,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, compression);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"A entrada {entry.FullName} excede o tamanho permitido.");
        }

        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (output.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"A entrada {entry.FullName} possui tamanho inconsistente.");
        }

        return output.ToArray();
    }

    private static async Task<(string Hash, long Length)> HashArchiveEntryAsync(
        ZipArchiveEntry entry,
        string? destinationPath,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var destination = destinationPath is null
            ? null
            : new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131_072,
                FileOptions.Asynchronous);
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131_072];
        var total = 0L;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            incremental.AppendData(buffer, 0, read);
            total = checked(total + read);
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (destination is not null)
        {
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return (Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant(), total);
    }

    private static async Task ValidateEvidenceFileAsync(
        string path,
        string expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                $"A evidência {expectedHash} não foi encontrada. Recapture o print.");
        }

        var info = new FileInfo(path);
        if (info.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"O tamanho da evidência {expectedHash} não corresponde. Recapture o print.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        if (!FixedTimeEqualsHex(actual, expectedHash))
        {
            throw new InvalidDataException(
                $"A evidência {expectedHash} foi alterada. Recapture o print.");
        }
    }

    private static void ValidateManifest(QuotationPackageManifest manifest)
    {
        if (!string.Equals(manifest.Kind, PackageKind, StringComparison.Ordinal) ||
            manifest.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                "O arquivo não é um pacote de cotação PNCP King compatível.");
        }

        if (manifest.BasketAlgorithmVersion != QuotationAnalyzer.BasketAlgorithmVersion)
        {
            throw new InvalidDataException(
                $"Versão do cálculo de cestas incompatível: " +
                $"{manifest.BasketAlgorithmVersion}. Esperada: " +
                $"{QuotationAnalyzer.BasketAlgorithmVersion}.");
        }

        if (manifest.DatabaseSchemaVersion < MinimumCompatibleDatabaseSchemaVersion ||
            manifest.DatabaseSchemaVersion > SqliteContractRepository.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Versão de dados incompatível: {manifest.DatabaseSchemaVersion}. " +
                $"Compatíveis: {MinimumCompatibleDatabaseSchemaVersion} a " +
                $"{SqliteContractRepository.CurrentSchemaVersion}.");
        }

        _ = NormalizeHash(manifest.DataSha256);
        if (manifest.ProjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.ProjectName) ||
            manifest.ItemCount < 0 ||
            manifest.ReferenceCount < 0 ||
            manifest.ManualBasketCount < 0)
        {
            throw new InvalidDataException("O manifesto contém dados inválidos.");
        }

        if (manifest.EvidenceAssets is null)
        {
            throw new InvalidDataException(
                "O manifesto não contém a lista de evidências.");
        }

        if (manifest.EvidenceAssets
            .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "O manifesto contém evidências duplicadas.");
        }

        foreach (var asset in manifest.EvidenceAssets)
        {
            var hash = NormalizeHash(asset.Sha256);
            if (asset.ByteLength <= 0 ||
                !string.Equals(
                    asset.ArchivePath,
                    EvidenceArchivePath(hash),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "O manifesto contém uma evidência inválida.");
            }
        }
    }

    private static QuotationLine MapLine(JsonObject row)
    {
        var cursorId = GetOptionalText(row, "search_cursor_pncp_id");
        return new QuotationLine
        {
            Id = ParseGuid(GetRequiredText(row, "id"), "item"),
            ProjectId = ParseGuid(GetRequiredText(row, "project_id"), "projeto"),
            Description = GetRequiredText(row, "description"),
            DisplayName = row.ContainsKey("display_name")
                ? GetRequiredText(row, "display_name")
                : GetRequiredText(row, "description"),
            RequestedQuantity = DecimalScale.FromScaled(
                GetRequiredLong(row, "requested_quantity_scaled"))!.Value,
            RequestedUnit = GetRequiredText(row, "requested_unit"),
            MinimumUnitPrice = DecimalScale.FromScaled(
                GetOptionalLong(row, "minimum_unit_price_scaled")),
            MaximumUnitPrice = DecimalScale.FromScaled(
                GetOptionalLong(row, "maximum_unit_price_scaled")),
            Weights = new AdequacyWeights(
                checked((int)GetRequiredLong(row, "description_weight")),
                checked((int)GetRequiredLong(row, "unit_weight")),
                checked((int)GetRequiredLong(row, "quantity_weight")),
                checked((int)GetRequiredLong(row, "proximity_weight")),
                checked((int)GetRequiredLong(row, "recency_weight"))),
            SampleVersion = checked((int)GetRequiredLong(row, "sample_version")),
            SampledAt = ParseDateTime(GetRequiredText(row, "sampled_at")),
            SelectedBasketKey = GetOptionalText(row, "selected_basket_key"),
            SelectionConfirmed = GetRequiredLong(row, "selection_confirmed") == 1,
            SearchText = GetRequiredText(row, "search_text"),
            RequestedBatchCount = checked((int)GetRequiredLong(row, "requested_batch_count")),
            DisplayOrder = checked((int)GetRequiredLong(row, "display_order")),
            AutomationRunId = ParseOptionalGuid(
                GetOptionalText(row, "automation_run_id"),
                "automação"),
            AutomationState = (QuotationAutomationItemState)GetRequiredLong(
                row,
                "automation_state"),
            AutomationMessage = GetRequiredText(row, "automation_message"),
            RequestedBasketSize = checked((int)GetRequiredLong(row, "requested_basket_size")),
            EstimatedUnitPrice = DecimalScale.FromScaled(
                GetOptionalLong(row, "estimated_unit_price_scaled")),
            EstimatedTotalPrice = DecimalScale.FromScaled(
                GetOptionalLong(row, "estimated_total_price_scaled")),
            UseEstimatedPrice = GetRequiredLong(row, "use_estimated_price") == 1,
            EstimateStage = (EstimateResolutionStage)GetRequiredLong(row, "estimate_stage"),
            SearchCheckpoint = new ItemSearchCheckpoint
            {
                RandomPivot = GetRequiredLong(row, "search_random_pivot"),
                Cursor = cursorId is null
                    ? null
                    : new ItemCandidateCursor(
                        checked((int)GetRequiredLong(row, "search_cursor_geo_layer")),
                        checked((int)GetRequiredLong(row, "search_cursor_group_rank")),
                        checked((int)GetRequiredLong(row, "search_cursor_rotation_band")),
                        GetRequiredLong(row, "search_cursor_random_key"),
                        cursorId),
                ContractsExamined = checked((int)GetRequiredLong(
                    row,
                    "search_contracts_examined")),
                BatchesCompleted = checked((int)GetRequiredLong(
                    row,
                    "search_batches_completed")),
                CandidateSetExhausted = GetRequiredLong(
                    row,
                    "search_candidate_exhausted") == 1,
                EstimateStage = (EstimateResolutionStage)GetRequiredLong(
                    row,
                    "estimate_stage")
            }
        };
    }

    private static QuotationReference MapReference(JsonObject row) => new()
    {
        Id = GetRequiredText(row, "id"),
        LineId = ParseGuid(GetRequiredText(row, "line_id"), "item da referência"),
        ContractId = GetRequiredText(row, "contract_id"),
        ItemNumber = GetRequiredLong(row, "item_number"),
        ResultSequence = GetRequiredLong(row, "result_sequence"),
        SupplierName = GetRequiredText(row, "supplier_name"),
        SupplierTaxId = GetRequiredText(row, "supplier_tax_id"),
        SupplierType = GetRequiredText(row, "supplier_type"),
        SupplierMunicipality = row.ContainsKey("supplier_municipality")
            ? GetOptionalText(row, "supplier_municipality") ?? string.Empty
            : string.Empty,
        SupplierUf = row.ContainsKey("supplier_uf")
            ? GetOptionalText(row, "supplier_uf") ?? string.Empty
            : string.Empty,
        HomologatedQuantity = DecimalScale.FromScaled(
            GetOptionalLong(row, "homologated_quantity_scaled")),
        UnitPrice = DecimalScale.FromScaled(GetRequiredLong(row, "unit_price_scaled"))!.Value,
        ResultDate = ParseOptionalDate(GetOptionalText(row, "result_date")),
        ItemDescription = GetRequiredText(row, "item_description"),
        ItemAdditionalInformation = GetRequiredText(row, "item_additional_information"),
        ItemUnit = GetRequiredText(row, "item_unit"),
        ItemRequestedQuantity = DecimalScale.FromScaled(
            GetOptionalLong(row, "item_requested_quantity_scaled")),
        ItemCategory = GetRequiredText(row, "item_category"),
        NcmNbsCode = GetRequiredText(row, "ncm_nbs_code"),
        NcmNbsDescription = GetRequiredText(row, "ncm_nbs_description"),
        CatalogCode = GetRequiredText(row, "catalog_code"),
        CatalogName = GetRequiredText(row, "catalog_name"),
        CatalogCategory = GetRequiredText(row, "catalog_category"),
        Organization = GetRequiredText(row, "organization"),
        Municipality = GetRequiredText(row, "municipality"),
        Uf = GetRequiredText(row, "uf"),
        DistanceFromRibeiraoKilometers = GetOptionalDouble(row, "distance_ribeirao_km"),
        PublicationDate = ParseOptionalDateTime(GetOptionalText(row, "publication_date")),
        PortalUrl = GetRequiredText(row, "portal_url"),
        Adequacy = new AdequacyBreakdown(
            DecimalScale.FromScaled(GetRequiredLong(row, "description_score_scaled"))!.Value,
            DecimalScale.FromScaled(GetRequiredLong(row, "unit_score_scaled"))!.Value,
            DecimalScale.FromScaled(GetRequiredLong(row, "quantity_score_scaled"))!.Value,
            DecimalScale.FromScaled(GetRequiredLong(row, "proximity_score_scaled"))!.Value,
            DecimalScale.FromScaled(GetRequiredLong(row, "recency_score_scaled"))!.Value,
            GetRequiredText(row, "explanation")),
        State = (QuotationReferenceState)GetRequiredLong(row, "state"),
        StateReason = GetRequiredText(row, "state_reason"),
        DuplicateOfReferenceId = GetOptionalText(row, "duplicate_of_reference_id"),
        MatchedPromptLevel = GetOptionalLong(row, "prompt_match_level") is { } prompt
            ? (PromptMatchLevel)prompt
            : null,
        MatchedSearchText = GetRequiredText(row, "matched_search_text"),
        Source = (QuotationReferenceSource)GetRequiredLong(row, "source_kind")
    };

    private static List<JsonObject> GetRows(JsonObject payload, string table) =>
        GetRowsFromTables(
            payload["tables"] as JsonObject
            ?? throw new InvalidDataException("O pacote não contém tabelas."),
            table);

    private static List<JsonObject> GetOptionalRows(JsonObject payload, string table)
    {
        var tables = payload["tables"] as JsonObject
                     ?? throw new InvalidDataException("O pacote não contém tabelas.");
        return tables[table] is null ? [] : GetRowsFromTables(tables, table);
    }

    private static List<JsonObject> GetRowsFromTables(JsonObject tables, string table)
    {
        var array = tables[table] as JsonArray
                    ?? throw new InvalidDataException(
                        $"O pacote não contém a tabela {table}.");
        return array.Select(node =>
                node as JsonObject
                ?? throw new InvalidDataException(
                    $"A tabela {table} contém uma linha inválida."))
            .ToList();
    }

    private static void EnsureUniqueRows(
        IReadOnlyList<JsonObject> rows,
        string table,
        IReadOnlyList<string> keyColumns)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = string.Join(
                '\u001f',
                keyColumns.Select(column => ValueKey(row[column])));
            if (!keys.Add(key))
            {
                throw new InvalidDataException(
                    $"A tabela {table} contém identificadores duplicados.");
            }
        }
    }

    private static Dictionary<string, string> CreateGuidMap(
        IReadOnlyList<JsonObject> rows,
        string column) =>
        rows.ToDictionary(
            row => GetRequiredText(row, column),
            _ => Guid.NewGuid().ToString("N"),
            StringComparer.Ordinal);

    private static void Remap(
        IReadOnlyList<JsonObject> rows,
        string column,
        IReadOnlyDictionary<string, string> map)
    {
        foreach (var row in rows)
        {
            var current = GetRequiredText(row, column);
            if (!map.TryGetValue(current, out var replacement))
            {
                throw new InvalidDataException(
                    $"Não foi possível remapear a coluna {column}.");
            }

            row[column] = replacement;
        }
    }

    private static void RemapOptional(
        IReadOnlyList<JsonObject> rows,
        string column,
        IReadOnlyDictionary<string, string> map)
    {
        foreach (var row in rows)
        {
            var current = GetOptionalText(row, column);
            if (current is null)
            {
                continue;
            }

            if (!map.TryGetValue(current, out var replacement))
            {
                throw new InvalidDataException(
                    $"Não foi possível remapear a coluna opcional {column}.");
            }

            row[column] = replacement;
        }
    }

    private static object ToDatabaseValue(JsonNode? node)
    {
        if (node is null)
        {
            return DBNull.Value;
        }

        if (node is not JsonValue value)
        {
            throw new InvalidDataException("O pacote contém um valor não escalar.");
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<int>(out var integer32))
        {
            return (long)integer32;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return real;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new InvalidDataException("O pacote contém um tipo de valor não suportado.");
    }

    private static string GetRequiredText(JsonObject row, string property)
    {
        if (!row.ContainsKey(property) ||
            row[property] is not JsonValue value ||
            !value.TryGetValue<string>(out var result))
        {
            throw new InvalidDataException(
                $"O campo obrigatório {property} é inválido.");
        }

        return result;
    }

    private static string? GetOptionalText(JsonObject row, string property)
    {
        if (!row.ContainsKey(property))
        {
            throw new InvalidDataException($"O campo {property} está ausente.");
        }

        if (row[property] is null)
        {
            return null;
        }

        if (row[property] is JsonValue value &&
            value.TryGetValue<string>(out var result))
        {
            return result;
        }

        throw new InvalidDataException($"O campo {property} é inválido.");
    }

    private static long GetRequiredLong(JsonObject row, string property)
    {
        if (!row.ContainsKey(property) ||
            row[property] is not JsonValue value)
        {
            throw new InvalidDataException(
                $"O campo numérico obrigatório {property} é inválido.");
        }

        if (value.TryGetValue<long>(out var result))
        {
            return result;
        }

        if (value.TryGetValue<int>(out var result32))
        {
            return result32;
        }

        throw new InvalidDataException(
            $"O campo numérico obrigatório {property} é inválido.");
    }

    private static long? GetOptionalLong(JsonObject row, string property)
    {
        if (!row.ContainsKey(property))
        {
            throw new InvalidDataException($"O campo {property} está ausente.");
        }

        if (row[property] is null)
        {
            return null;
        }

        if (row[property] is JsonValue value && value.TryGetValue<long>(out var result))
        {
            return result;
        }

        if (row[property] is JsonValue value32 && value32.TryGetValue<int>(out var result32))
        {
            return result32;
        }

        throw new InvalidDataException($"O campo numérico {property} é inválido.");
    }

    private static double? GetOptionalDouble(JsonObject row, string property)
    {
        if (!row.ContainsKey(property))
        {
            throw new InvalidDataException($"O campo {property} está ausente.");
        }

        if (row[property] is null)
        {
            return null;
        }

        if (row[property] is JsonValue value)
        {
            if (value.TryGetValue<double>(out var result))
            {
                return result;
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer;
            }
        }

        throw new InvalidDataException($"O campo real {property} é inválido.");
    }

    private static string ValueKey(JsonNode? node) =>
        node is null ? "<null>" : node.ToJsonString();

    private static void EnsureGuid(JsonObject row, string property, string label) =>
        _ = ParseGuid(GetRequiredText(row, property), label);

    private static Guid ParseGuid(string value, string label) =>
        Guid.TryParseExact(value, "N", out var result) && result != Guid.Empty
            ? result
            : throw new InvalidDataException(
                $"O identificador de {label} é inválido.");

    private static Guid? ParseOptionalGuid(string? value, string label) =>
        value is null ? null : ParseGuid(value, label);

    private static DateTimeOffset ParseDateTime(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : throw new InvalidDataException("O pacote contém uma data inválida.");

    private static DateTimeOffset? ParseOptionalDateTime(string? value) =>
        value is null ? null : ParseDateTime(value);

    private static DateOnly? ParseOptionalDate(string? value) =>
        value is null
            ? null
            : DateOnly.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : throw new InvalidDataException("O pacote contém uma data inválida.");

    private static void EnsureSame(string actual, string expected, string relationship)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"O relacionamento {relationship} é incompatível.");
        }
    }

    private static void EnsureContains(
        IReadOnlySet<string> values,
        string value,
        string message)
    {
        if (!values.Contains(value))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureOptionalAsset(
        JsonObject row,
        string property,
        IReadOnlySet<string> assets)
    {
        var value = GetOptionalText(row, property);
        if (value is not null)
        {
            EnsureContains(
                assets,
                NormalizeHash(value),
                "Um rascunho web aponta para um print ausente.");
        }
    }

    private static void EnsureAsset(
        JsonObject row,
        string property,
        IReadOnlySet<string> assets) =>
        EnsureContains(
            assets,
            NormalizeHash(GetRequiredText(row, property)),
            "Uma evidência web aponta para um print ausente.");

    private static string Composite(string left, string right) => $"{left}\u001f{right}";

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedTimeEqualsHex(string left, string right)
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

    private static string NormalizeHash(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("O pacote contém um hash SHA-256 inválido.");
        }

        return normalized;
    }

    private static string EvidenceArchivePath(string hash) =>
        $"internet-evidence/{hash}.png";

    private static string EnsureExtension(string path) =>
        path.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path + PackageExtension);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"pncpking-quotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = "<>:\"/\\|?*".ToHashSet();
        var sanitized = new string(value
            .Select(character =>
                character < ' ' || invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Cotacao" : sanitized;
    }

    private sealed record TableDefinition(
        string Name,
        string ExportQuery,
        IReadOnlyList<string> KeyColumns,
        bool SkipsMissingContracts = false,
        bool UpsertsExistingRows = false);

    private sealed record LoadedPackage(
        QuotationPackageManifest Manifest,
        JsonObject Payload);

    private sealed record QuotationPackageManifest
    {
        public string Kind { get; init; } = string.Empty;
        public int FormatVersion { get; init; }
        public int BasketAlgorithmVersion { get; init; }
        public int DatabaseSchemaVersion { get; init; }
        public string AppVersion { get; init; } = string.Empty;
        public DateTimeOffset ExportedAt { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectName { get; init; } = string.Empty;
        public int ItemCount { get; init; }
        public int ReferenceCount { get; init; }
        public int ManualBasketCount { get; init; }
        public bool HasIncompleteAutomation { get; init; }
        public string DataSha256 { get; init; } = string.Empty;
        public IReadOnlyList<QuotationPackageAssetManifest> EvidenceAssets { get; init; } = [];
    }

    private sealed record QuotationPackageAssetManifest
    {
        public string Sha256 { get; init; } = string.Empty;
        public string ArchivePath { get; init; } = string.Empty;
        public long ByteLength { get; init; }
    }
}
