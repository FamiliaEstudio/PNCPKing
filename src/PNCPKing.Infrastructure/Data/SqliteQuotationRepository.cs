using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed class SqliteQuotationRepository : IQuotationRepository
{
    private readonly string _connectionString;

    public SqliteQuotationRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public async Task<IReadOnlyList<QuotationProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_at, updated_at FROM quotation_projects ORDER BY updated_at DESC, name;";
        var projects = new List<QuotationProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<QuotationProject> CreateProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = DateTimeOffset.UtcNow;
        var project = new QuotationProject(Guid.NewGuid(), name.Trim(), now, now);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO quotation_projects(id, name, created_at, updated_at) VALUES($id, $name, $created, $updated);";
        command.Parameters.AddWithValue("$id", project.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$created", FormatDateTime(project.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDateTime(project.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_id, description, requested_quantity_scaled, requested_unit,
                   minimum_unit_price_scaled, maximum_unit_price_scaled, description_weight,
                   unit_weight, quantity_weight, proximity_weight, recency_weight, sample_version,
                   sampled_at, selected_basket_key, selection_confirmed
              FROM quotation_lines
             WHERE project_id = $projectId
             ORDER BY sampled_at, id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
        var lines = new List<QuotationLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(ReadLine(reader));
        }

        return lines;
    }

    public async Task<IReadOnlyList<QuotationReference>> GetReferencesAsync(Guid lineId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ReferenceSelectSql + " WHERE line_id = $lineId ORDER BY id;";
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        var references = new List<QuotationReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            references.Add(ReadReference(reader));
        }

        return references;
    }

    public async Task<QuotationLine> SaveSampleAsync(
        Guid projectId,
        Guid? lineId,
        QuotationLineInput input,
        IReadOnlyList<QuotationReference> references,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var id = lineId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.Transaction = (SqliteTransaction)transaction;
            lineCommand.CommandText = """
                INSERT INTO quotation_lines(
                    id, project_id, description, requested_quantity_scaled, requested_unit,
                    minimum_unit_price_scaled, maximum_unit_price_scaled, description_weight,
                    unit_weight, quantity_weight, proximity_weight, recency_weight, sample_version,
                    sampled_at, selected_basket_key, selection_confirmed)
                VALUES($id, $projectId, $description, $quantity, $unit, $minimum, $maximum,
                       $descriptionWeight, $unitWeight, $quantityWeight, $proximityWeight, $recencyWeight,
                       1, $sampledAt, NULL, 0)
                ON CONFLICT(id) DO UPDATE SET
                    description = excluded.description,
                    requested_quantity_scaled = excluded.requested_quantity_scaled,
                    requested_unit = excluded.requested_unit,
                    minimum_unit_price_scaled = excluded.minimum_unit_price_scaled,
                    maximum_unit_price_scaled = excluded.maximum_unit_price_scaled,
                    description_weight = excluded.description_weight,
                    unit_weight = excluded.unit_weight,
                    quantity_weight = excluded.quantity_weight,
                    proximity_weight = excluded.proximity_weight,
                    recency_weight = excluded.recency_weight,
                    sample_version = quotation_lines.sample_version + 1,
                    sampled_at = excluded.sampled_at,
                    selection_confirmed = 0;
                """;
            lineCommand.Parameters.AddWithValue("$id", id.ToString("N"));
            lineCommand.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
            lineCommand.Parameters.AddWithValue("$description", input.Description.Trim());
            lineCommand.Parameters.AddWithValue("$quantity", DecimalScale.ToScaled(input.RequestedQuantity)!.Value);
            lineCommand.Parameters.AddWithValue("$unit", input.RequestedUnit.Trim());
            lineCommand.Parameters.AddWithValue("$minimum", DbValue(DecimalScale.ToScaled(input.MinimumUnitPrice)));
            lineCommand.Parameters.AddWithValue("$maximum", DbValue(DecimalScale.ToScaled(input.MaximumUnitPrice)));
            lineCommand.Parameters.AddWithValue("$descriptionWeight", input.Weights.Description);
            lineCommand.Parameters.AddWithValue("$unitWeight", input.Weights.Unit);
            lineCommand.Parameters.AddWithValue("$quantityWeight", input.Weights.Quantity);
            lineCommand.Parameters.AddWithValue("$proximityWeight", input.Weights.Proximity);
            lineCommand.Parameters.AddWithValue("$recencyWeight", input.Weights.Recency);
            lineCommand.Parameters.AddWithValue("$sampledAt", FormatDateTime(now));
            await lineCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM quotation_references WHERE line_id = $lineId;";
            delete.Parameters.AddWithValue("$lineId", id.ToString("N"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertReferencesAsync(connection, (SqliteTransaction)transaction, id, references, cancellationToken).ConfigureAwait(false);
        await using (var project = connection.CreateCommand())
        {
            project.Transaction = (SqliteTransaction)transaction;
            project.CommandText = "UPDATE quotation_projects SET updated_at = $updated WHERE id = $projectId;";
            project.Parameters.AddWithValue("$updated", FormatDateTime(now));
            project.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
            if (await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("O projeto de cotação não existe mais.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var lines = await GetLinesAsync(projectId, cancellationToken).ConfigureAwait(false);
        return lines.Single(line => line.Id == id);
    }

    public async Task ConfirmBasketAsync(Guid lineId, string basketKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basketKey);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotation_lines
               SET selected_basket_key = $basketKey, selection_confirmed = 1
             WHERE id = $lineId;
            """;
        command.Parameters.AddWithValue("$basketKey", basketKey);
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("O item da cotação não existe mais.");
        }
    }

    public async Task UpdateWeightsAsync(
        Guid lineId,
        AdequacyWeights weights,
        CancellationToken cancellationToken = default)
    {
        weights.Validate();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var line = connection.CreateCommand())
        {
            line.Transaction = (SqliteTransaction)transaction;
            line.CommandText = """
                UPDATE quotation_lines
                   SET description_weight = $description,
                       unit_weight = $unit,
                       quantity_weight = $quantity,
                       proximity_weight = $proximity,
                       recency_weight = $recency,
                       selection_confirmed = 0
                 WHERE id = $lineId;
                """;
            line.Parameters.AddWithValue("$description", weights.Description);
            line.Parameters.AddWithValue("$unit", weights.Unit);
            line.Parameters.AddWithValue("$quantity", weights.Quantity);
            line.Parameters.AddWithValue("$proximity", weights.Proximity);
            line.Parameters.AddWithValue("$recency", weights.Recency);
            line.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            if (await line.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("O item da cotação não existe mais.");
            }
        }

        await using (var project = connection.CreateCommand())
        {
            project.Transaction = (SqliteTransaction)transaction;
            project.CommandText = """
                UPDATE quotation_projects
                   SET updated_at = $updated
                 WHERE id = (SELECT project_id FROM quotation_lines WHERE id = $lineId);
                """;
            project.Parameters.AddWithValue("$updated", FormatDateTime(now));
            project.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid lineId,
        IReadOnlyList<QuotationReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quotation_references(
                id, line_id, contract_id, item_number, result_sequence, supplier_name,
                supplier_tax_id, supplier_type, homologated_quantity_scaled, unit_price_scaled,
                result_date, item_description, item_additional_information, item_unit,
                item_requested_quantity_scaled, item_category, ncm_nbs_code, ncm_nbs_description,
                catalog_code, catalog_name, catalog_category, organization, municipality, uf,
                distance_ribeirao_km, publication_date, portal_url, description_score_scaled,
                unit_score_scaled, quantity_score_scaled, proximity_score_scaled, recency_score_scaled,
                explanation, state, state_reason, duplicate_of_reference_id)
            VALUES($id, $lineId, $contractId, $itemNumber, $resultSequence, $supplierName,
                   $supplierTaxId, $supplierType, $homologatedQuantity, $unitPrice, $resultDate,
                   $itemDescription, $itemAdditional, $itemUnit, $itemRequestedQuantity,
                   $itemCategory, $ncmNbsCode, $ncmNbsDescription, $catalogCode, $catalogName,
                   $catalogCategory, $organization, $municipality, $uf, $distance, $publicationDate,
                   $portalUrl, $descriptionScore, $unitScore, $quantityScore, $proximityScore,
                   $recencyScore, $explanation, $state, $stateReason, $duplicateOf);
            """;
        foreach (var name in new[]
                 {
                     "$id", "$lineId", "$contractId", "$itemNumber", "$resultSequence", "$supplierName",
                     "$supplierTaxId", "$supplierType", "$homologatedQuantity", "$unitPrice", "$resultDate",
                     "$itemDescription", "$itemAdditional", "$itemUnit", "$itemRequestedQuantity", "$itemCategory",
                     "$ncmNbsCode", "$ncmNbsDescription", "$catalogCode", "$catalogName", "$catalogCategory",
                     "$organization", "$municipality", "$uf", "$distance", "$publicationDate", "$portalUrl",
                     "$descriptionScore", "$unitScore", "$quantityScore", "$proximityScore", "$recencyScore",
                     "$explanation", "$state", "$stateReason", "$duplicateOf"
                 })
        {
            command.Parameters.Add(name, SqliteType.Text);
        }

        foreach (var reference in references)
        {
            command.Parameters["$id"].Value = reference.Id;
            command.Parameters["$lineId"].Value = lineId.ToString("N");
            command.Parameters["$contractId"].Value = reference.ContractId;
            command.Parameters["$itemNumber"].Value = reference.ItemNumber;
            command.Parameters["$resultSequence"].Value = reference.ResultSequence;
            command.Parameters["$supplierName"].Value = reference.SupplierName;
            command.Parameters["$supplierTaxId"].Value = reference.SupplierTaxId;
            command.Parameters["$supplierType"].Value = reference.SupplierType;
            command.Parameters["$homologatedQuantity"].Value = DbValue(DecimalScale.ToScaled(reference.HomologatedQuantity));
            command.Parameters["$unitPrice"].Value = DecimalScale.ToScaled(reference.UnitPrice)!.Value;
            command.Parameters["$resultDate"].Value = DbValue(reference.ResultDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters["$itemDescription"].Value = reference.ItemDescription;
            command.Parameters["$itemAdditional"].Value = reference.ItemAdditionalInformation;
            command.Parameters["$itemUnit"].Value = reference.ItemUnit;
            command.Parameters["$itemRequestedQuantity"].Value = DbValue(DecimalScale.ToScaled(reference.ItemRequestedQuantity));
            command.Parameters["$itemCategory"].Value = reference.ItemCategory;
            command.Parameters["$ncmNbsCode"].Value = reference.NcmNbsCode;
            command.Parameters["$ncmNbsDescription"].Value = reference.NcmNbsDescription;
            command.Parameters["$catalogCode"].Value = reference.CatalogCode;
            command.Parameters["$catalogName"].Value = reference.CatalogName;
            command.Parameters["$catalogCategory"].Value = reference.CatalogCategory;
            command.Parameters["$organization"].Value = reference.Organization;
            command.Parameters["$municipality"].Value = reference.Municipality;
            command.Parameters["$uf"].Value = reference.Uf;
            command.Parameters["$distance"].Value = DbValue(reference.DistanceFromRibeiraoKilometers);
            command.Parameters["$publicationDate"].Value = DbValue(reference.PublicationDate?.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters["$portalUrl"].Value = reference.PortalUrl;
            command.Parameters["$descriptionScore"].Value = DecimalScale.ToScaled(reference.Adequacy.DescriptionScore)!.Value;
            command.Parameters["$unitScore"].Value = DecimalScale.ToScaled(reference.Adequacy.UnitScore)!.Value;
            command.Parameters["$quantityScore"].Value = DecimalScale.ToScaled(reference.Adequacy.QuantityScore)!.Value;
            command.Parameters["$proximityScore"].Value = DecimalScale.ToScaled(reference.Adequacy.ProximityScore)!.Value;
            command.Parameters["$recencyScore"].Value = DecimalScale.ToScaled(reference.Adequacy.RecencyScore)!.Value;
            command.Parameters["$explanation"].Value = reference.Adequacy.Explanation;
            command.Parameters["$state"].Value = (int)reference.State;
            command.Parameters["$stateReason"].Value = reference.StateReason;
            command.Parameters["$duplicateOf"].Value = DbValue(reference.DuplicateOfReferenceId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static QuotationProject ReadProject(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        ParseDateTime(reader.GetString(2)),
        ParseDateTime(reader.GetString(3)));

    private static QuotationLine ReadLine(SqliteDataReader reader) => new()
    {
        Id = Guid.ParseExact(reader.GetString(0), "N"),
        ProjectId = Guid.ParseExact(reader.GetString(1), "N"),
        Description = reader.GetString(2),
        RequestedQuantity = DecimalScale.FromScaled(reader.GetInt64(3))!.Value,
        RequestedUnit = reader.GetString(4),
        MinimumUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 5)),
        MaximumUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 6)),
        Weights = new AdequacyWeights(
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11)),
        SampleVersion = reader.GetInt32(12),
        SampledAt = ParseDateTime(reader.GetString(13)),
        SelectedBasketKey = reader.IsDBNull(14) ? null : reader.GetString(14),
        SelectionConfirmed = reader.GetInt64(15) == 1
    };

    private static QuotationReference ReadReference(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        LineId = Guid.ParseExact(reader.GetString(1), "N"),
        ContractId = reader.GetString(2),
        ItemNumber = reader.GetInt64(3),
        ResultSequence = reader.GetInt64(4),
        SupplierName = reader.GetString(5),
        SupplierTaxId = reader.GetString(6),
        SupplierType = reader.GetString(7),
        HomologatedQuantity = DecimalScale.FromScaled(ReadNullableLong(reader, 8)),
        UnitPrice = DecimalScale.FromScaled(reader.GetInt64(9))!.Value,
        ResultDate = ParseDate(reader, 10),
        ItemDescription = reader.GetString(11),
        ItemAdditionalInformation = reader.GetString(12),
        ItemUnit = reader.GetString(13),
        ItemRequestedQuantity = DecimalScale.FromScaled(ReadNullableLong(reader, 14)),
        ItemCategory = reader.GetString(15),
        NcmNbsCode = reader.GetString(16),
        NcmNbsDescription = reader.GetString(17),
        CatalogCode = reader.GetString(18),
        CatalogName = reader.GetString(19),
        CatalogCategory = reader.GetString(20),
        Organization = reader.GetString(21),
        Municipality = reader.GetString(22),
        Uf = reader.GetString(23),
        DistanceFromRibeiraoKilometers = reader.IsDBNull(24) ? null : reader.GetDouble(24),
        PublicationDate = reader.IsDBNull(25) ? null : ParseDateTime(reader.GetString(25)),
        PortalUrl = reader.GetString(26),
        Adequacy = new AdequacyBreakdown(
            DecimalScale.FromScaled(reader.GetInt64(27))!.Value,
            DecimalScale.FromScaled(reader.GetInt64(28))!.Value,
            DecimalScale.FromScaled(reader.GetInt64(29))!.Value,
            DecimalScale.FromScaled(reader.GetInt64(30))!.Value,
            DecimalScale.FromScaled(reader.GetInt64(31))!.Value,
            reader.GetString(32)),
        State = (QuotationReferenceState)reader.GetInt32(33),
        StateReason = reader.GetString(34),
        DuplicateOfReferenceId = reader.IsDBNull(35) ? null : reader.GetString(35)
    };

    private static void ValidateInput(QuotationLineInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.RequestedUnit);
        if (input.RequestedQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "A quantidade deve ser maior que zero.");
        }

        if (input.MinimumUnitPrice < 0 || input.MaximumUnitPrice < 0 ||
            input.MinimumUnitPrice is not null && input.MaximumUnitPrice is not null && input.MinimumUnitPrice > input.MaximumUnitPrice)
        {
            throw new ArgumentException("A faixa de preço da cotação é inválida.", nameof(input));
        }

        input.Weights.Validate();
    }

    private static DateTimeOffset ParseDateTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || !DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? null
            : date;

    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static string FormatDateTime(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static object DbValue(object? value) => value ?? DBNull.Value;

    private const string ReferenceSelectSql = """
        SELECT id, line_id, contract_id, item_number, result_sequence, supplier_name,
               supplier_tax_id, supplier_type, homologated_quantity_scaled, unit_price_scaled,
               result_date, item_description, item_additional_information, item_unit,
               item_requested_quantity_scaled, item_category, ncm_nbs_code, ncm_nbs_description,
               catalog_code, catalog_name, catalog_category, organization, municipality, uf,
               distance_ribeirao_km, publication_date, portal_url, description_score_scaled,
               unit_score_scaled, quantity_score_scaled, proximity_score_scaled, recency_score_scaled,
               explanation, state, state_reason, duplicate_of_reference_id
          FROM quotation_references
        """;
}
