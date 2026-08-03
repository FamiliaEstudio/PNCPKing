using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed class SqliteQuotationRepository : IQuotationRepository, IQuotationItemSearchRepository
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

    public async Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE quotation_projects SET name = $name, updated_at = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", projectId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A cotação não existe mais.");
        }
    }

    public async Task RenameLineDisplayNameAsync(
        Guid lineId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.Join(
            ' ',
            (displayName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            throw new ArgumentException("O nome do item não pode ficar vazio.", nameof(displayName));
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var line = connection.CreateCommand())
        {
            line.Transaction = (SqliteTransaction)transaction;
            line.CommandText = "UPDATE quotation_lines SET display_name = $name WHERE id = $id;";
            line.Parameters.AddWithValue("$name", normalized);
            line.Parameters.AddWithValue("$id", lineId.ToString("N"));
            if (await line.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("O item da cotação não existe mais.");
            }
        }

        await TouchLineProjectAsync(connection, (SqliteTransaction)transaction, lineId, now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLineCatalogSelectionAsync(
        Guid lineId,
        QuotationCatalogSelection? selection,
        CancellationToken cancellationToken = default)
    {
        if (selection is not null &&
            (string.IsNullOrWhiteSpace(selection.Code) || string.IsNullOrWhiteSpace(selection.Description)))
        {
            throw new ArgumentException("O código e a descrição do catálogo são obrigatórios.", nameof(selection));
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = selection is null
                ? "DELETE FROM quotation_catalog_selections WHERE line_id = $lineId;"
                : """
                  INSERT INTO quotation_catalog_selections(
                      line_id, catalog_kind, catalog_code, description_snapshot, selected_at)
                  VALUES($lineId, $kind, $code, $description, $selectedAt)
                  ON CONFLICT(line_id) DO UPDATE SET
                      catalog_kind = excluded.catalog_kind,
                      catalog_code = excluded.catalog_code,
                      description_snapshot = excluded.description_snapshot,
                      selected_at = excluded.selected_at;
                  """;
            command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            if (selection is not null)
            {
                command.Parameters.AddWithValue("$kind", (int)selection.Kind);
                command.Parameters.AddWithValue("$code", selection.Code.Trim());
                command.Parameters.AddWithValue("$description", selection.Description.Trim());
                command.Parameters.AddWithValue("$selectedAt", FormatDateTime(now));
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchLineProjectAsync(connection, (SqliteTransaction)transaction, lineId, now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quotation_projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteLineAsync(Guid lineId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM quotation_lines WHERE id = $id;";
            command.Parameters.AddWithValue("$id", lineId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuotationItemSearchWorkspace?> GetWorkspaceAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT line_id, prompt_slot, search_text, geo_filter_kind, geo_filter_uf,
                   start_date, end_date, sort_kind,
                   minimum_unit_price_scaled, maximum_unit_price_scaled, batch_count,
                   random_pivot, cursor_geo_layer, cursor_group_rank, cursor_rotation_band,
                   cursor_random_key, cursor_pncp_id, contracts_examined, batches_completed,
                   candidate_set_exhausted, matched_items, revealed_prices,
                   item_lists_from_cache, item_lists_from_api, item_result_api_calls,
                   failed_calls, status_message, updated_at
              FROM quotation_item_search_workspaces
             WHERE line_id = $lineId AND prompt_slot = $slot;
            """;
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        command.Parameters.AddWithValue("$slot", (int)slot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadItemSearchWorkspace(reader)
            : null;
    }

    public async Task<IReadOnlyList<QuotationItemSearchHit>> GetWorkspaceHitsAsync(
        Guid lineId,
        ItemSearchPromptSlot slot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT line_id, prompt_slot, contract_id, item_number,
                   matched_prompt_level, matched_search_text, discovered_order
              FROM quotation_item_search_hits
             WHERE line_id = $lineId AND prompt_slot = $slot
             ORDER BY discovered_order, contract_id, item_number;
            """;
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        command.Parameters.AddWithValue("$slot", (int)slot);
        var values = new List<QuotationItemSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new QuotationItemSearchHit
            {
                LineId = Guid.ParseExact(reader.GetString(0), "N"),
                Slot = (ItemSearchPromptSlot)reader.GetInt32(1),
                ContractId = reader.GetString(2),
                ItemNumber = reader.GetInt64(3),
                MatchedPromptLevel = reader.IsDBNull(4)
                    ? null
                    : (PromptMatchLevel)reader.GetInt32(4),
                MatchedSearchText = reader.GetString(5),
                DiscoveredOrder = reader.GetInt64(6)
            });
        }

        return values;
    }

    public async Task SaveWorkspaceAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        SetWorkspaceCommand(command, workspace);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveProcessedContractAsync(
        QuotationItemSearchWorkspace workspace,
        IReadOnlyList<QuotationItemSearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(hits);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var saveWorkspace = connection.CreateCommand())
        {
            saveWorkspace.Transaction = (SqliteTransaction)transaction;
            SetWorkspaceCommand(saveWorkspace, workspace);
            await saveWorkspace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO quotation_item_search_hits(
                    line_id, prompt_slot, contract_id, item_number, matched_prompt_level,
                    matched_search_text, discovered_order)
                VALUES($lineId, $slot, $contractId, $itemNumber, $level, $text, $order)
                ON CONFLICT(line_id, prompt_slot, contract_id, item_number) DO UPDATE SET
                    matched_prompt_level = excluded.matched_prompt_level,
                    matched_search_text = excluded.matched_search_text,
                    discovered_order = MIN(
                        quotation_item_search_hits.discovered_order,
                        excluded.discovered_order);
                """;
            insert.Parameters.Add("$lineId", SqliteType.Text);
            insert.Parameters.Add("$slot", SqliteType.Integer);
            insert.Parameters.Add("$contractId", SqliteType.Text);
            insert.Parameters.Add("$itemNumber", SqliteType.Integer);
            insert.Parameters.Add("$level", SqliteType.Integer);
            insert.Parameters.Add("$text", SqliteType.Text);
            insert.Parameters.Add("$order", SqliteType.Integer);
            foreach (var hit in hits)
            {
                insert.Parameters["$lineId"].Value = workspace.LineId.ToString("N");
                insert.Parameters["$slot"].Value = (int)workspace.Slot;
                insert.Parameters["$contractId"].Value = hit.ContractId;
                insert.Parameters["$itemNumber"].Value = hit.ItemNumber;
                insert.Parameters["$level"].Value = DbValue(hit.MatchedPromptLevel is null
                    ? null
                    : (int)hit.MatchedPromptLevel.Value);
                insert.Parameters["$text"].Value = hit.MatchedSearchText;
                insert.Parameters["$order"].Value = hit.DiscoveredOrder;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetWorkspaceAsync(
        QuotationItemSearchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM quotation_item_search_hits
                 WHERE line_id = $lineId AND prompt_slot = $slot;
                """;
            delete.Parameters.AddWithValue("$lineId", workspace.LineId.ToString("N"));
            delete.Parameters.AddWithValue("$slot", (int)workspace.Slot);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var save = connection.CreateCommand())
        {
            save.Transaction = (SqliteTransaction)transaction;
            SetWorkspaceCommand(save, workspace);
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.id, l.project_id, l.description, l.requested_quantity_scaled, l.requested_unit,
                   l.minimum_unit_price_scaled, l.maximum_unit_price_scaled, l.description_weight,
                   l.unit_weight, l.quantity_weight, l.proximity_weight, l.recency_weight, l.sample_version,
                   l.sampled_at, l.selected_basket_key, l.selection_confirmed, l.search_text,
                   l.requested_batch_count, l.display_order, l.automation_run_id, l.automation_state,
                   l.automation_message, l.requested_basket_size, l.estimated_unit_price_scaled,
                   l.estimated_total_price_scaled, l.use_estimated_price, l.estimate_stage,
                   l.search_random_pivot, l.search_cursor_geo_layer, l.search_cursor_group_rank,
                   l.search_cursor_rotation_band, l.search_cursor_random_key, l.search_cursor_pncp_id,
                   l.search_contracts_examined, l.search_batches_completed, l.search_candidate_exhausted,
                   p.version, p.restrictive_text, p.intermediate_text, p.broad_text,
                   p.origin, p.validation_state, p.active_level, p.contracts_at_level,
                   p.matched_items, p.revealed_prices, p.updated_at,
                   l.display_name, s.catalog_kind, s.catalog_code,
                   s.description_snapshot, s.selected_at,
                   CASE WHEN ce.code IS NULL THEN 1 ELSE ce.active END
              FROM quotation_lines l
              LEFT JOIN quotation_line_search_prompts p
                ON p.line_id = l.id AND p.is_current = 1
              LEFT JOIN quotation_catalog_selections s ON s.line_id = l.id
              LEFT JOIN catalog_entries ce
                ON ce.catalog_kind = s.catalog_kind AND ce.code = s.catalog_code
             WHERE l.project_id = $projectId
             ORDER BY l.display_order, l.sampled_at, l.id;
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

    public async Task<IReadOnlyList<QuotationManualBasket>> GetManualBasketsAsync(
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var baskets = new List<(Guid Id, string Name, int DisplayOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, display_order, created_at, updated_at
                  FROM quotation_manual_baskets
                 WHERE line_id = $lineId
                 ORDER BY display_order, name, id;
                """;
            command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                baskets.Add((
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    ParseDateTime(reader.GetString(3)),
                    ParseDateTime(reader.GetString(4))));
            }
        }

        var result = new List<QuotationManualBasket>(baskets.Count);
        foreach (var basket in baskets)
        {
            await using var references = connection.CreateCommand();
            references.CommandText = """
                SELECT reference_id
                  FROM quotation_manual_basket_references
                 WHERE basket_id = $basketId
                 ORDER BY display_order, reference_id;
                """;
            references.Parameters.AddWithValue("$basketId", basket.Id.ToString("N"));
            var referenceIds = new List<string>();
            await using var reader = await references.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                referenceIds.Add(reader.GetString(0));
            }

            result.Add(new QuotationManualBasket
            {
                Id = basket.Id,
                LineId = lineId,
                Name = basket.Name,
                ReferenceIds = referenceIds,
                DisplayOrder = basket.DisplayOrder,
                CreatedAt = basket.CreatedAt,
                UpdatedAt = basket.UpdatedAt
            });
        }

        return result;
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
                    id, project_id, description, display_name, requested_quantity_scaled, requested_unit,
                    minimum_unit_price_scaled, maximum_unit_price_scaled, description_weight,
                    unit_weight, quantity_weight, proximity_weight, recency_weight, sample_version,
                    sampled_at, selected_basket_key, selection_confirmed, search_text,
                    requested_batch_count, display_order, automation_run_id, automation_state,
                    automation_message, requested_basket_size)
                VALUES($id, $projectId, $description, $description, $quantity, $unit, $minimum, $maximum,
                       $descriptionWeight, $unitWeight, $quantityWeight, $proximityWeight, $recencyWeight,
                       1, $sampledAt, NULL, 0, $description, 1,
                       COALESCE((SELECT MAX(display_order) + 1 FROM quotation_lines WHERE project_id = $projectId), 0),
                       NULL, 0, '', $basketSize)
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
                    requested_basket_size = excluded.requested_basket_size,
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
            lineCommand.Parameters.AddWithValue("$basketSize", input.RequestedBasketSize);
            lineCommand.Parameters.AddWithValue("$sampledAt", FormatDateTime(now));
            await lineCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<QuotationManualBasket> SaveManualBasketAsync(
        Guid lineId,
        Guid? basketId,
        string name,
        IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(referenceIds);
        var uniqueReferenceIds = referenceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uniqueReferenceIds.Length == 0)
        {
            throw new ArgumentException("Selecione pelo menos um preço homologado.", nameof(referenceIds));
        }

        var id = basketId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (basketId is not null)
        {
            await using var ownership = connection.CreateCommand();
            ownership.Transaction = (SqliteTransaction)transaction;
            ownership.CommandText = "SELECT line_id FROM quotation_manual_baskets WHERE id = $id;";
            ownership.Parameters.AddWithValue("$id", id.ToString("N"));
            var owner = await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (owner is not string ownerId || ownerId != lineId.ToString("N"))
            {
                throw new InvalidOperationException("A cesta manual não pertence ao item selecionado.");
            }
        }

        await using (var validate = connection.CreateCommand())
        {
            validate.Transaction = (SqliteTransaction)transaction;
            var parameterNames = uniqueReferenceIds.Select((_, index) => $"$reference{index}").ToArray();
            validate.CommandText = $"""
                SELECT COUNT(*)
                  FROM quotation_references
                 WHERE line_id = $lineId AND id IN ({string.Join(", ", parameterNames)});
                """;
            validate.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            for (var index = 0; index < uniqueReferenceIds.Length; index++)
            {
                validate.Parameters.AddWithValue(parameterNames[index], uniqueReferenceIds[index]);
            }

            var count = Convert.ToInt32(
                await validate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (count != uniqueReferenceIds.Length)
            {
                throw new InvalidOperationException("Uma ou mais referências não pertencem ao item da cotação.");
            }
        }

        await using (var basket = connection.CreateCommand())
        {
            basket.Transaction = (SqliteTransaction)transaction;
            basket.CommandText = """
                INSERT INTO quotation_manual_baskets(
                    id, line_id, name, display_order, created_at, updated_at)
                VALUES(
                    $id, $lineId, $name,
                    COALESCE((SELECT MAX(display_order) + 1
                                FROM quotation_manual_baskets
                               WHERE line_id = $lineId), 0),
                    $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    updated_at = excluded.updated_at;
                """;
            basket.Parameters.AddWithValue("$id", id.ToString("N"));
            basket.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            basket.Parameters.AddWithValue("$name", name.Trim());
            basket.Parameters.AddWithValue("$created", FormatDateTime(now));
            basket.Parameters.AddWithValue("$updated", FormatDateTime(now));
            await basket.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM quotation_manual_basket_references WHERE basket_id = $basketId;";
            delete.Parameters.AddWithValue("$basketId", id.ToString("N"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO quotation_manual_basket_references(
                    basket_id, line_id, reference_id, display_order)
                VALUES($basketId, $lineId, $referenceId, $displayOrder);
                """;
            insert.Parameters.Add("$basketId", SqliteType.Text);
            insert.Parameters.Add("$lineId", SqliteType.Text);
            insert.Parameters.Add("$referenceId", SqliteType.Text);
            insert.Parameters.Add("$displayOrder", SqliteType.Integer);
            for (var index = 0; index < uniqueReferenceIds.Length; index++)
            {
                insert.Parameters["$basketId"].Value = id.ToString("N");
                insert.Parameters["$lineId"].Value = lineId.ToString("N");
                insert.Parameters["$referenceId"].Value = uniqueReferenceIds[index];
                insert.Parameters["$displayOrder"].Value = index;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await TouchLineAndProjectAsync(
            connection,
            (SqliteTransaction)transaction,
            lineId,
            clearSelection: true,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetManualBasketsAsync(lineId, cancellationToken).ConfigureAwait(false))
            .Single(basket => basket.Id == id);
    }

    public async Task RenameManualBasketAsync(
        Guid basketId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotation_manual_baskets
               SET name = $name, updated_at = $updated
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", basketId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A cesta manual não existe mais.");
        }
    }

    public async Task RemoveManualBasketReferenceAsync(
        Guid basketId,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid lineId;
        await using (var owner = connection.CreateCommand())
        {
            owner.Transaction = (SqliteTransaction)transaction;
            owner.CommandText = "SELECT line_id FROM quotation_manual_baskets WHERE id = $id;";
            owner.Parameters.AddWithValue("$id", basketId.ToString("N"));
            var value = await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            lineId = value is string text
                ? Guid.ParseExact(text, "N")
                : throw new InvalidOperationException("A cesta manual não existe mais.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM quotation_manual_basket_references
                 WHERE basket_id = $basketId AND reference_id = $referenceId;
                """;
            delete.Parameters.AddWithValue("$basketId", basketId.ToString("N"));
            delete.Parameters.AddWithValue("$referenceId", referenceId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = (SqliteTransaction)transaction;
            cleanup.CommandText = """
                DELETE FROM quotation_manual_baskets
                 WHERE id = $basketId
                   AND NOT EXISTS(
                       SELECT 1 FROM quotation_manual_basket_references
                        WHERE basket_id = $basketId);
                """;
            cleanup.Parameters.AddWithValue("$basketId", basketId.ToString("N"));
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchLineAndProjectAsync(
            connection,
            (SqliteTransaction)transaction,
            lineId,
            clearSelection: true,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteManualBasketAsync(Guid basketId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid lineId;
        await using (var owner = connection.CreateCommand())
        {
            owner.Transaction = (SqliteTransaction)transaction;
            owner.CommandText = "SELECT line_id FROM quotation_manual_baskets WHERE id = $id;";
            owner.Parameters.AddWithValue("$id", basketId.ToString("N"));
            var value = await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not string text)
            {
                return;
            }

            lineId = Guid.ParseExact(text, "N");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM quotation_manual_baskets WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", basketId.ToString("N"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchLineAndProjectAsync(
            connection,
            (SqliteTransaction)transaction,
            lineId,
            clearSelection: true,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InternetPriceDraft>> GetInternetPriceDraftsAsync(
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.line_id, d.basket_id, d.source_url, d.unit_price_scaled,
                   d.description, d.supplier_name, d.supplier_tax_id, d.captured_at,
                   d.created_at, d.updated_at,
                   p.sha256, p.relative_path, p.mime_type, p.byte_length,
                   p.pixel_width, p.pixel_height, p.created_at,
                   t.sha256, t.relative_path, t.mime_type, t.byte_length,
                   t.pixel_width, t.pixel_height, t.created_at
              FROM quotation_internet_price_drafts d
              LEFT JOIN quotation_internet_evidence_assets p
                ON p.sha256 = d.price_image_sha256
              LEFT JOIN quotation_internet_evidence_assets t
                ON t.sha256 = d.tax_id_image_sha256
             WHERE d.line_id = $lineId
             ORDER BY d.updated_at DESC, d.id;
            """;
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        var drafts = new List<InternetPriceDraft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            drafts.Add(new InternetPriceDraft
            {
                Id = Guid.ParseExact(reader.GetString(0), "N"),
                LineId = Guid.ParseExact(reader.GetString(1), "N"),
                BasketId = reader.IsDBNull(2) ? null : Guid.ParseExact(reader.GetString(2), "N"),
                SourceUrl = reader.GetString(3),
                UnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 4)),
                Description = reader.GetString(5),
                SupplierName = reader.GetString(6),
                SupplierTaxId = reader.GetString(7),
                CapturedAt = ParseDateTime(reader.GetString(8)),
                CreatedAt = ParseDateTime(reader.GetString(9)),
                UpdatedAt = ParseDateTime(reader.GetString(10)),
                PriceImage = ReadEvidenceImage(reader, 11),
                TaxIdImage = ReadEvidenceImage(reader, 18)
            });
        }

        return drafts;
    }

    public async Task<InternetPriceDraft> SaveInternetPriceDraftAsync(
        InternetPriceDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = DateTimeOffset.UtcNow;
        var normalized = draft with
        {
            SourceUrl = draft.SourceUrl.Trim(),
            Description = draft.Description.Trim(),
            SupplierName = draft.SupplierName.Trim(),
            SupplierTaxId = NormalizeTaxId(draft.SupplierTaxId),
            CapturedAt = draft.CapturedAt == default ? now : draft.CapturedAt,
            CreatedAt = draft.CreatedAt == default ? now : draft.CreatedAt,
            UpdatedAt = now
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertEvidenceAssetAsync(
            connection,
            (SqliteTransaction)transaction,
            normalized.PriceImage,
            cancellationToken).ConfigureAwait(false);
        await UpsertEvidenceAssetAsync(
            connection,
            (SqliteTransaction)transaction,
            normalized.TaxIdImage,
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO quotation_internet_price_drafts(
                id, line_id, basket_id, source_url, unit_price_scaled, description,
                supplier_name, supplier_tax_id, price_image_sha256, tax_id_image_sha256,
                captured_at, created_at, updated_at)
            VALUES($id, $lineId, $basketId, $url, $price, $description, $supplier,
                   $taxId, $priceImage, $taxImage, $captured, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                basket_id = excluded.basket_id,
                source_url = excluded.source_url,
                unit_price_scaled = excluded.unit_price_scaled,
                description = excluded.description,
                supplier_name = excluded.supplier_name,
                supplier_tax_id = excluded.supplier_tax_id,
                price_image_sha256 = excluded.price_image_sha256,
                tax_id_image_sha256 = excluded.tax_id_image_sha256,
                captured_at = excluded.captured_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", normalized.Id.ToString("N"));
        command.Parameters.AddWithValue("$lineId", normalized.LineId.ToString("N"));
        command.Parameters.AddWithValue("$basketId", DbValue(normalized.BasketId?.ToString("N")));
        command.Parameters.AddWithValue("$url", normalized.SourceUrl);
        command.Parameters.AddWithValue("$price", DbValue(DecimalScale.ToScaled(normalized.UnitPrice)));
        command.Parameters.AddWithValue("$description", normalized.Description);
        command.Parameters.AddWithValue("$supplier", normalized.SupplierName);
        command.Parameters.AddWithValue("$taxId", normalized.SupplierTaxId);
        command.Parameters.AddWithValue("$priceImage", DbValue(normalized.PriceImage?.Sha256));
        command.Parameters.AddWithValue("$taxImage", DbValue(normalized.TaxIdImage?.Sha256));
        command.Parameters.AddWithValue("$captured", FormatDateTime(normalized.CapturedAt));
        command.Parameters.AddWithValue("$created", FormatDateTime(normalized.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDateTime(normalized.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async Task DeleteInternetPriceDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quotation_internet_price_drafts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", draftId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, InternetPriceEvidence>> GetInternetPriceEvidenceAsync(
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.reference_id, e.source_url, e.captured_at,
                   p.sha256, p.relative_path, p.mime_type, p.byte_length,
                   p.pixel_width, p.pixel_height, p.created_at,
                   t.sha256, t.relative_path, t.mime_type, t.byte_length,
                   t.pixel_width, t.pixel_height, t.created_at
              FROM quotation_internet_price_evidence e
              JOIN quotation_internet_evidence_assets p
                ON p.sha256 = e.price_image_sha256
              JOIN quotation_internet_evidence_assets t
                ON t.sha256 = e.tax_id_image_sha256
             WHERE e.line_id = $lineId
             ORDER BY e.reference_id;
            """;
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        var evidence = new Dictionary<string, InternetPriceEvidence>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var referenceId = reader.GetString(0);
            evidence.Add(referenceId, new InternetPriceEvidence
            {
                LineId = lineId,
                ReferenceId = referenceId,
                SourceUrl = reader.GetString(1),
                CapturedAt = ParseDateTime(reader.GetString(2)),
                PriceImage = ReadEvidenceImage(reader, 3)!,
                TaxIdImage = ReadEvidenceImage(reader, 10)!
            });
        }

        return evidence;
    }

    public async Task<QuotationManualBasket> SaveInternetPriceReferenceAsync(
        QuotationReference reference,
        InternetPriceEvidence evidence,
        Guid basketId,
        string basketName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(basketName);
        if (reference.Source != QuotationReferenceSource.InternetIncisoIII ||
            reference.LineId != evidence.LineId ||
            !string.Equals(reference.Id, evidence.ReferenceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A referência e as evidências da internet não correspondem.");
        }

        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await UpsertEvidenceAssetAsync(
                connection,
                (SqliteTransaction)transaction,
                evidence.PriceImage,
                cancellationToken).ConfigureAwait(false);
            await UpsertEvidenceAssetAsync(
                connection,
                (SqliteTransaction)transaction,
                evidence.TaxIdImage,
                cancellationToken).ConfigureAwait(false);
            await InsertReferencesAsync(
                connection,
                (SqliteTransaction)transaction,
                reference.LineId,
                [reference],
                cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO quotation_internet_price_evidence(
                    line_id, reference_id, source_url, captured_at,
                    price_image_sha256, tax_id_image_sha256)
                VALUES($lineId, $referenceId, $url, $captured, $priceImage, $taxImage)
                ON CONFLICT(line_id, reference_id) DO UPDATE SET
                    source_url = excluded.source_url,
                    captured_at = excluded.captured_at,
                    price_image_sha256 = excluded.price_image_sha256,
                    tax_id_image_sha256 = excluded.tax_id_image_sha256;
                """;
            command.Parameters.AddWithValue("$lineId", reference.LineId.ToString("N"));
            command.Parameters.AddWithValue("$referenceId", reference.Id);
            command.Parameters.AddWithValue("$url", evidence.SourceUrl);
            command.Parameters.AddWithValue("$captured", FormatDateTime(evidence.CapturedAt));
            command.Parameters.AddWithValue("$priceImage", evidence.PriceImage.Sha256);
            command.Parameters.AddWithValue("$taxImage", evidence.TaxIdImage.Sha256);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var baskets = await GetManualBasketsAsync(reference.LineId, cancellationToken).ConfigureAwait(false);
        var existing = baskets.SingleOrDefault(item => item.Id == basketId);
        var referenceIds = (existing?.ReferenceIds ?? [])
            .Append(reference.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await SaveManualBasketAsync(
            reference.LineId,
            existing?.Id,
            existing?.Name ?? basketName,
            referenceIds,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteInternetPriceReferenceAsync(
        Guid lineId,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM quotation_references
                 WHERE line_id = $lineId AND id = $referenceId AND source_kind = 1;
                """;
            delete.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            delete.Parameters.AddWithValue("$referenceId", referenceId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var emptyBaskets = connection.CreateCommand())
        {
            emptyBaskets.Transaction = (SqliteTransaction)transaction;
            emptyBaskets.CommandText = """
                DELETE FROM quotation_manual_baskets
                 WHERE line_id = $lineId
                   AND NOT EXISTS(
                       SELECT 1
                         FROM quotation_manual_basket_references members
                        WHERE members.basket_id = quotation_manual_baskets.id);
                """;
            emptyBaskets.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            await emptyBaskets.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchLineAndProjectAsync(
            connection,
            (SqliteTransaction)transaction,
            lineId,
            clearSelection: true,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetReferencedInternetEvidenceHashesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT price_image_sha256 FROM quotation_internet_price_drafts
             WHERE price_image_sha256 IS NOT NULL
            UNION
            SELECT tax_id_image_sha256 FROM quotation_internet_price_drafts
             WHERE tax_id_image_sha256 IS NOT NULL
            UNION
            SELECT price_image_sha256 FROM quotation_internet_price_evidence
            UNION
            SELECT tax_id_image_sha256 FROM quotation_internet_price_evidence;
            """;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
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

    public Task<QuotationAutomationRun> CreateAutomationRunAsync(
        Guid projectId,
        string outputPath,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        CancellationToken cancellationToken = default) =>
        CreateAutomationRunCoreAsync(
            projectId,
            outputPath,
            geoFilter,
            startDate,
            endDate,
            items,
            weights,
            QuotationAutomationMode.FixedBatches,
            TimeSpan.Zero,
            null,
            null,
            null,
            cancellationToken);

    public Task<QuotationAutomationRun> CreateTimedAutomationRunAsync(
        Guid projectId,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        TimeSpan timeBudget,
        IReadOnlyList<string>? contractSearchPrompts = null,
        Guid? sourceDraftId = null,
        string? sourcePdfSha256 = null,
        CancellationToken cancellationToken = default) =>
        CreateAutomationRunCoreAsync(
            projectId,
            string.Empty,
            geoFilter,
            startDate,
            endDate,
            items,
            weights,
            QuotationAutomationMode.TimedRoundRobin,
            timeBudget,
            contractSearchPrompts,
            sourceDraftId,
            sourcePdfSha256,
            cancellationToken);

    private async Task<QuotationAutomationRun> CreateAutomationRunCoreAsync(
        Guid projectId,
        string outputPath,
        SearchGeoFilter geoFilter,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<QuotationImportItem> items,
        AdequacyWeights weights,
        QuotationAutomationMode mode,
        TimeSpan timeBudget,
        IReadOnlyList<string>? contractSearchPrompts,
        Guid? sourceDraftId,
        string? sourcePdfSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geoFilter);
        ArgumentNullException.ThrowIfNull(items);
        if (mode == QuotationAutomationMode.FixedBatches)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        }
        else if (timeBudget < TimeSpan.FromMinutes(5) || timeBudget > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeBudget),
                "O tempo da automação com IA deve ficar entre 5 minutos e 24 horas.");
        }

        if (items.Count == 0)
        {
            throw new ArgumentException("A importação não contém itens.", nameof(items));
        }

        weights.Validate();
        var now = DateTimeOffset.UtcNow;
        var run = new QuotationAutomationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OutputPath = mode == QuotationAutomationMode.FixedBatches ? Path.GetFullPath(outputPath) : string.Empty,
            GeoFilter = geoFilter,
            StartDate = startDate,
            EndDate = endDate,
            State = QuotationAutomationRunState.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Mode = mode,
            TimeBudget = timeBudget,
            ActiveElapsed = TimeSpan.Zero,
            SourceDraftId = sourceDraftId,
            SourcePdfSha256 = sourcePdfSha256 ?? string.Empty,
            StrategyVersion = mode == QuotationAutomationMode.TimedRoundRobin ? 3 : 0
        };
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var runCommand = connection.CreateCommand())
        {
            runCommand.Transaction = (SqliteTransaction)transaction;
            runCommand.CommandText = """
                INSERT INTO quotation_automation_runs(
                    id, project_id, output_path, geo_filter_kind, geo_filter_uf,
                    start_date, end_date, state, message, created_at, updated_at,
                    automation_mode, time_budget_seconds, active_elapsed_seconds,
                    source_draft_id, source_pdf_sha256, strategy_version)
                VALUES($id, $projectId, $output, $geoKind, $geoUf, $start, $end, $state, '', $created, $updated,
                       $mode, $timeBudget, 0, $sourceDraftId, $sourcePdfSha256, $strategyVersion);
                """;
            runCommand.Parameters.AddWithValue("$id", run.Id.ToString("N"));
            runCommand.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
            runCommand.Parameters.AddWithValue("$output", run.OutputPath);
            runCommand.Parameters.AddWithValue("$geoKind", (int)geoFilter.Kind);
            runCommand.Parameters.AddWithValue("$geoUf", DbValue(geoFilter.Uf));
            runCommand.Parameters.AddWithValue("$start", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            runCommand.Parameters.AddWithValue("$end", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            runCommand.Parameters.AddWithValue("$state", (int)run.State);
            runCommand.Parameters.AddWithValue("$mode", (int)run.Mode);
            runCommand.Parameters.AddWithValue("$timeBudget", Convert.ToInt64(run.TimeBudget.TotalSeconds));
            runCommand.Parameters.AddWithValue("$sourceDraftId", DbValue(sourceDraftId?.ToString("N")));
            runCommand.Parameters.AddWithValue("$sourcePdfSha256", run.SourcePdfSha256);
            runCommand.Parameters.AddWithValue("$strategyVersion", run.StrategyVersion);
            runCommand.Parameters.AddWithValue("$created", FormatDateTime(now));
            runCommand.Parameters.AddWithValue("$updated", FormatDateTime(now));
            await runCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var nextOrder = 0;
        await using (var orderCommand = connection.CreateCommand())
        {
            orderCommand.Transaction = (SqliteTransaction)transaction;
            orderCommand.CommandText = "SELECT COALESCE(MAX(display_order) + 1, 0) FROM quotation_lines WHERE project_id = $projectId;";
            orderCommand.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
            nextOrder = Convert.ToInt32(
                await orderCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        foreach (var item in items)
        {
            if (item.RequestedBasketSize is < 3 or > 10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    $"A linha {item.SourceRow:N0} deve solicitar de 3 a 10 preços por cesta.");
            }

            var lineId = Guid.NewGuid();
            await using var line = connection.CreateCommand();
            line.Transaction = (SqliteTransaction)transaction;
            line.CommandText = """
                INSERT INTO quotation_lines(
                    id, project_id, description, display_name, requested_quantity_scaled, requested_unit,
                    minimum_unit_price_scaled, maximum_unit_price_scaled, description_weight,
                    unit_weight, quantity_weight, proximity_weight, recency_weight, sample_version,
                    sampled_at, selected_basket_key, selection_confirmed, search_text,
                    requested_batch_count, display_order, automation_run_id, automation_state,
                    automation_message, requested_basket_size, estimated_unit_price_scaled,
                    estimated_total_price_scaled, use_estimated_price, estimate_stage,
                    search_random_pivot, search_contracts_examined, search_batches_completed,
                    search_candidate_exhausted)
                VALUES($id, $projectId, $description, $description, $quantity, $unit, $minimum, $maximum,
                       $descriptionWeight, $unitWeight, $quantityWeight, $proximityWeight, $recencyWeight,
                       0, $sampledAt, NULL, 0, $searchText, $batches, $displayOrder, $runId, $state, '',
                       $basketSize, $estimatedUnit, $estimatedTotal, $useEstimated, $estimateStage,
                       0, 0, 0, 0);
                """;
            line.Parameters.AddWithValue("$id", lineId.ToString("N"));
            line.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
            line.Parameters.AddWithValue("$description", item.OutputDescription.Trim());
            line.Parameters.AddWithValue("$quantity", DecimalScale.ToScaled(item.Quantity)!.Value);
            line.Parameters.AddWithValue("$unit", item.Unit.Trim());
            line.Parameters.AddWithValue("$minimum", DbValue(DecimalScale.ToScaled(item.MinimumUnitPrice)));
            line.Parameters.AddWithValue("$maximum", DbValue(DecimalScale.ToScaled(item.MaximumUnitPrice)));
            line.Parameters.AddWithValue("$descriptionWeight", weights.Description);
            line.Parameters.AddWithValue("$unitWeight", weights.Unit);
            line.Parameters.AddWithValue("$quantityWeight", weights.Quantity);
            line.Parameters.AddWithValue("$proximityWeight", weights.Proximity);
            line.Parameters.AddWithValue("$recencyWeight", weights.Recency);
            line.Parameters.AddWithValue("$sampledAt", FormatDateTime(now));
            line.Parameters.AddWithValue("$searchText", item.SearchText.Trim());
            line.Parameters.AddWithValue("$batches", item.BatchCount);
            line.Parameters.AddWithValue("$basketSize", item.RequestedBasketSize);
            line.Parameters.AddWithValue("$estimatedUnit", DbValue(DecimalScale.ToScaled(item.EstimatedUnitPrice)));
            line.Parameters.AddWithValue("$estimatedTotal", DbValue(DecimalScale.ToScaled(item.EstimatedTotalPrice)));
            line.Parameters.AddWithValue("$useEstimated", item.UseEstimatedPrice ? 1 : 0);
            line.Parameters.AddWithValue(
                "$estimateStage",
                (int)(item.UseEstimatedPrice && item.EstimatedUnitPrice is > 0
                    ? EstimateResolutionStage.Within25Percent
                    : EstimateResolutionStage.NotApplicable));
            line.Parameters.AddWithValue("$displayOrder", nextOrder++);
            line.Parameters.AddWithValue("$runId", run.Id.ToString("N"));
            line.Parameters.AddWithValue("$state", (int)QuotationAutomationItemState.Pending);
            await line.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var promptSet = connection.CreateCommand();
            promptSet.Transaction = (SqliteTransaction)transaction;
            promptSet.CommandText = """
                UPDATE quotation_line_search_prompts
                   SET restrictive_text = $restrictive,
                       intermediate_text = $intermediate,
                       broad_text = $broad,
                       origin = $origin,
                       updated_at = $updated
                 WHERE line_id = $lineId AND is_current = 1;
                """;
            promptSet.Parameters.AddWithValue("$restrictive", item.SearchText.Trim());
            promptSet.Parameters.AddWithValue("$intermediate", item.IntermediateSearchText.Trim());
            promptSet.Parameters.AddWithValue("$broad", item.BroadSearchText.Trim());
            promptSet.Parameters.AddWithValue("$origin", (int)item.PromptOrigin);
            promptSet.Parameters.AddWithValue("$updated", FormatDateTime(now));
            promptSet.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            await promptSet.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (mode == QuotationAutomationMode.TimedRoundRobin)
        {
            var prompts = (contractSearchPrompts ?? [])
                .Select(value => value?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            if (prompts.Count == 0)
            {
                prompts.AddRange(items
                    .Select(item => string.IsNullOrWhiteSpace(item.BroadSearchText)
                        ? item.OutputDescription
                        : item.BroadSearchText)
                    .Select(ExtractContractFallbackPrompt)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10));
            }

            for (var index = 0; index < prompts.Count; index++)
            {
                var expression = PNCPKing.Core.Search.SearchText.Parse(prompts[index]);
                if (string.IsNullOrWhiteSpace(expression.ContractMatchQuery))
                {
                    continue;
                }

                await using var prompt = connection.CreateCommand();
                prompt.Transaction = (SqliteTransaction)transaction;
                prompt.CommandText = """
                    INSERT INTO quotation_contract_search_prompts(
                        run_id, display_order, prompt_text, random_pivot,
                        candidate_exhausted, contracts_examined, is_fallback)
                    VALUES($runId, $order, $text, $pivot, 0, 0, 0);
                    """;
                prompt.Parameters.AddWithValue("$runId", run.Id.ToString("N"));
                prompt.Parameters.AddWithValue("$order", index);
                prompt.Parameters.AddWithValue("$text", prompts[index]);
                prompt.Parameters.AddWithValue("$pivot", Random.Shared.NextInt64(1, long.MaxValue));
                await prompt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var project = connection.CreateCommand())
        {
            project.Transaction = (SqliteTransaction)transaction;
            project.CommandText = "UPDATE quotation_projects SET updated_at = $updated WHERE id = $id;";
            project.Parameters.AddWithValue("$updated", FormatDateTime(now));
            project.Parameters.AddWithValue("$id", projectId.ToString("N"));
            if (await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("A cotação de destino não existe mais.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<QuotationAutomationRun?> GetLatestAutomationRunAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_id, output_path, geo_filter_kind, geo_filter_uf,
                   start_date, end_date, state, message, created_at, updated_at,
                   automation_mode, time_budget_seconds, active_elapsed_seconds,
                   source_draft_id, source_pdf_sha256, strategy_version,
                   unique_contracts_processed, matched_items, revealed_prices,
                   item_list_cache_hits, item_list_api_calls, item_result_api_calls,
                   failed_calls, consecutive_no_results
              FROM quotation_automation_runs
             WHERE project_id = $projectId AND state <> $completed
             ORDER BY updated_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
        command.Parameters.AddWithValue("$completed", (int)QuotationAutomationRunState.Completed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAutomationRun(reader) : null;
    }

    public async Task RecoverInterruptedAutomationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lines = connection.CreateCommand())
        {
            lines.Transaction = (SqliteTransaction)transaction;
            lines.CommandText = "UPDATE quotation_lines SET automation_state = $pending, automation_message = 'Execução interrompida; pronta para retomar.' WHERE automation_state = $running;";
            lines.Parameters.AddWithValue("$pending", (int)QuotationAutomationItemState.Pending);
            lines.Parameters.AddWithValue("$running", (int)QuotationAutomationItemState.Running);
            await lines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var runs = connection.CreateCommand())
        {
            runs.Transaction = (SqliteTransaction)transaction;
            runs.CommandText = "UPDATE quotation_automation_runs SET state = $pending, message = 'Execução interrompida; pronta para retomar.' WHERE state = $running;";
            runs.Parameters.AddWithValue("$pending", (int)QuotationAutomationRunState.Pending);
            runs.Parameters.AddWithValue("$running", (int)QuotationAutomationRunState.Running);
            await runs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAutomationItemStateAsync(
        Guid lineId,
        QuotationAutomationItemState state,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE quotation_lines SET automation_state = $state, automation_message = $message WHERE id = $id;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$message", message ?? string.Empty);
        command.Parameters.AddWithValue("$id", lineId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAutomationRunStateAsync(
        Guid runId,
        QuotationAutomationRunState state,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE quotation_automation_runs SET state = $state, message = $message, updated_at = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$message", message ?? string.Empty);
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", runId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSearchCheckpointAsync(
        Guid lineId,
        ItemSearchCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotation_lines
               SET estimate_stage = $stage,
                   search_random_pivot = $pivot,
                   search_cursor_geo_layer = $geoLayer,
                   search_cursor_group_rank = $groupRank,
                   search_cursor_rotation_band = $rotationBand,
                   search_cursor_random_key = $randomKey,
                   search_cursor_pncp_id = $pncpId,
                   search_contracts_examined = $examined,
                   search_batches_completed = $batches,
                   search_candidate_exhausted = $exhausted
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$stage", (int)checkpoint.EstimateStage);
        command.Parameters.AddWithValue("$pivot", checkpoint.RandomPivot);
        command.Parameters.AddWithValue("$geoLayer", DbValue(checkpoint.Cursor?.GeographicLayer));
        command.Parameters.AddWithValue("$groupRank", DbValue(checkpoint.Cursor?.GroupRank));
        command.Parameters.AddWithValue("$rotationBand", DbValue(checkpoint.Cursor?.RotationBand));
        command.Parameters.AddWithValue("$randomKey", DbValue(checkpoint.Cursor?.RandomOrderKey));
        command.Parameters.AddWithValue("$pncpId", DbValue(checkpoint.Cursor?.PncpId));
        command.Parameters.AddWithValue("$examined", Math.Max(0, checkpoint.ContractsExamined));
        command.Parameters.AddWithValue("$batches", Math.Max(0, checkpoint.BatchesCompleted));
        command.Parameters.AddWithValue("$exhausted", checkpoint.CandidateSetExhausted ? 1 : 0);
        command.Parameters.AddWithValue("$id", lineId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("O item da cotação não existe mais.");
        }
    }

    public async Task UpdateAutomationTimingAsync(
        Guid runId,
        TimeSpan activeElapsed,
        TimeSpan? newTimeBudget = null,
        CancellationToken cancellationToken = default)
    {
        if (activeElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activeElapsed));
        }

        if (newTimeBudget is not null &&
            (newTimeBudget < TimeSpan.FromMinutes(5) || newTimeBudget > TimeSpan.FromHours(24)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(newTimeBudget),
                "O tempo total deve ficar entre 5 minutos e 24 horas.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = newTimeBudget is null
            ? """
              UPDATE quotation_automation_runs
                 SET active_elapsed_seconds = $elapsed, updated_at = $updated
               WHERE id = $id;
              """
            : """
              UPDATE quotation_automation_runs
                 SET active_elapsed_seconds = $elapsed, time_budget_seconds = $budget,
                     updated_at = $updated
               WHERE id = $id;
              """;
        command.Parameters.AddWithValue("$elapsed", Convert.ToInt64(activeElapsed.TotalSeconds));
        if (newTimeBudget is not null)
        {
            command.Parameters.AddWithValue("$budget", Convert.ToInt64(newTimeBudget.Value.TotalSeconds));
        }

        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", runId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A automação não existe mais.");
        }
    }

    public async Task UpdateAutomationOutputPathAsync(
        Guid runId,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var normalized = Path.GetFullPath(outputPath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotation_automation_runs
               SET output_path = $output,
                   updated_at = $updated
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$output", normalized);
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", runId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A automação não existe mais.");
        }
    }

    public async Task UpgradeContractSearchStrategyAsync(
        Guid runId,
        int strategyVersion,
        CancellationToken cancellationToken = default)
    {
        if (strategyVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(strategyVersion));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var prompts = connection.CreateCommand())
        {
            prompts.Transaction = (SqliteTransaction)transaction;
            prompts.CommandText = """
                UPDATE quotation_contract_search_prompts
                   SET cursor_geo_layer = NULL,
                       cursor_group_rank = NULL,
                       cursor_rotation_band = NULL,
                       cursor_random_key = NULL,
                       cursor_pncp_id = NULL,
                       candidate_exhausted = 0
                 WHERE run_id = $runId
                   AND EXISTS(
                       SELECT 1
                         FROM quotation_automation_runs run
                        WHERE run.id = $runId
                          AND run.strategy_version < $strategyVersion);
                """;
            prompts.Parameters.AddWithValue("$runId", runId.ToString("N"));
            prompts.Parameters.AddWithValue("$strategyVersion", strategyVersion);
            await prompts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var run = connection.CreateCommand())
        {
            run.Transaction = (SqliteTransaction)transaction;
            run.CommandText = """
                UPDATE quotation_automation_runs
                   SET strategy_version = MAX(strategy_version, $strategyVersion),
                       updated_at = $updated
                 WHERE id = $runId;
                """;
            run.Parameters.AddWithValue("$strategyVersion", strategyVersion);
            run.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
            run.Parameters.AddWithValue("$runId", runId.ToString("N"));
            if (await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("A automação não existe mais.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task LinkAutomationDraftAsync(
        Guid runId,
        Guid draftId,
        string pdfSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfSha256);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotation_automation_runs
               SET source_draft_id = $draftId,
                   source_pdf_sha256 = $pdfSha256,
                   updated_at = $updated
             WHERE id = $runId;
            """;
        command.Parameters.AddWithValue("$draftId", draftId.ToString("N"));
        command.Parameters.AddWithValue("$pdfSha256", pdfSha256.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$runId", runId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A automação não existe mais.");
        }
    }

    public async Task<ItemSearchPromptSet> GetItemSearchPromptSetAsync(
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, restrictive_text, intermediate_text, broad_text,
                   origin, validation_state, active_level, contracts_at_level,
                   matched_items, revealed_prices, updated_at
              FROM quotation_line_search_prompts
             WHERE line_id = $lineId AND is_current = 1;
            """;
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("O item da cotação não possui critérios de pesquisa.");
        }

        return ReadPromptSet(reader, lineId);
    }

    public async Task SaveItemSearchPromptSetAsync(
        ItemSearchPromptSet promptSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promptSet);
        foreach (var text in new[]
                 {
                     promptSet.RestrictiveText,
                     promptSet.IntermediateText,
                     promptSet.BroadText
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            _ = PNCPKing.Core.Search.SearchText.Parse(text);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentVersion = 0;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = (SqliteTransaction)transaction;
            current.CommandText = """
                SELECT COALESCE(MAX(version), 0)
                  FROM quotation_line_search_prompts
                 WHERE line_id = $lineId;
                """;
            current.Parameters.AddWithValue("$lineId", promptSet.LineId.ToString("N"));
            currentVersion = Convert.ToInt32(
                await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        await using (var retire = connection.CreateCommand())
        {
            retire.Transaction = (SqliteTransaction)transaction;
            retire.CommandText = """
                UPDATE quotation_line_search_prompts
                   SET is_current = 0
                 WHERE line_id = $lineId AND is_current = 1;
                """;
            retire.Parameters.AddWithValue("$lineId", promptSet.LineId.ToString("N"));
            await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO quotation_line_search_prompts(
                    line_id, version, restrictive_text, intermediate_text, broad_text,
                    origin, validation_state, active_level, contracts_at_level,
                    matched_items, revealed_prices, updated_at, is_current)
                VALUES($lineId, $version, $restrictive, $intermediate, $broad,
                       $origin, $validation, $level, $contracts, $matched,
                       $revealed, $updated, 1);
                """;
            insert.Parameters.AddWithValue("$lineId", promptSet.LineId.ToString("N"));
            insert.Parameters.AddWithValue("$version", currentVersion + 1);
            insert.Parameters.AddWithValue("$restrictive", promptSet.RestrictiveText.Trim());
            insert.Parameters.AddWithValue("$intermediate", promptSet.IntermediateText.Trim());
            insert.Parameters.AddWithValue("$broad", promptSet.BroadText.Trim());
            insert.Parameters.AddWithValue("$origin", (int)promptSet.Origin);
            insert.Parameters.AddWithValue("$validation", (int)promptSet.ValidationState);
            insert.Parameters.AddWithValue("$level", (int)promptSet.ActiveLevel);
            insert.Parameters.AddWithValue("$contracts", Math.Max(0, promptSet.ContractsAtActiveLevel));
            insert.Parameters.AddWithValue("$matched", Math.Max(0, promptSet.MatchedItems));
            insert.Parameters.AddWithValue("$revealed", Math.Max(0, promptSet.RevealedPrices));
            insert.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateItemSearchPromptProgressAsync(
        Guid lineId,
        PromptMatchLevel activeLevel,
        int contractsAtActiveLevel,
        int matchedItems,
        int revealedPrices,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotation_line_search_prompts
               SET active_level = $level,
                   contracts_at_level = $contracts,
                   matched_items = $matched,
                   revealed_prices = $revealed,
                   updated_at = $updated
             WHERE line_id = $lineId AND is_current = 1;
            """;
        command.Parameters.AddWithValue("$level", (int)activeLevel);
        command.Parameters.AddWithValue("$contracts", Math.Max(0, contractsAtActiveLevel));
        command.Parameters.AddWithValue("$matched", Math.Max(0, matchedItems));
        command.Parameters.AddWithValue("$revealed", Math.Max(0, revealedPrices));
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("O item da cotação não possui critérios de pesquisa atuais.");
        }

        await using var legacyCheckpoint = connection.CreateCommand();
        legacyCheckpoint.CommandText = """
            UPDATE quotation_lines
               SET search_contracts_examined = search_contracts_examined + 1,
                   search_batches_completed = (search_contracts_examined + 1) / 50
             WHERE id = $lineId;
            """;
        legacyCheckpoint.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        await legacyCheckpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContractSearchPrompt>> GetContractSearchPromptsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT display_order, prompt_text, random_pivot,
                   cursor_geo_layer, cursor_group_rank, cursor_rotation_band,
                   cursor_random_key, cursor_pncp_id, candidate_exhausted,
                   contracts_examined, is_fallback
              FROM quotation_contract_search_prompts
             WHERE run_id = $runId
             ORDER BY display_order;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString("N"));
        var result = new List<ContractSearchPrompt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ContractSearchPrompt
            {
                RunId = runId,
                DisplayOrder = reader.GetInt32(0),
                Text = reader.GetString(1),
                RandomPivot = reader.GetInt64(2),
                Cursor = reader.IsDBNull(7)
                    ? null
                    : new ItemCandidateCursor(
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetString(7)),
                CandidateSetExhausted = reader.GetInt64(8) == 1,
                ContractsExamined = reader.GetInt32(9),
                IsFallback = reader.GetInt64(10) == 1
            });
        }

        return result;
    }

    public async Task SaveContractSearchPromptAsync(
        ContractSearchPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quotation_contract_search_prompts(
                run_id, display_order, prompt_text, random_pivot,
                cursor_geo_layer, cursor_group_rank, cursor_rotation_band,
                cursor_random_key, cursor_pncp_id, candidate_exhausted,
                contracts_examined, is_fallback)
            VALUES($runId, $order, $text, $pivot, $layer, $group, $band,
                   $random, $pncpId, $exhausted, $examined, $fallback)
            ON CONFLICT(run_id, display_order) DO UPDATE SET
                prompt_text = excluded.prompt_text,
                random_pivot = excluded.random_pivot,
                cursor_geo_layer = excluded.cursor_geo_layer,
                cursor_group_rank = excluded.cursor_group_rank,
                cursor_rotation_band = excluded.cursor_rotation_band,
                cursor_random_key = excluded.cursor_random_key,
                cursor_pncp_id = excluded.cursor_pncp_id,
                candidate_exhausted = excluded.candidate_exhausted,
                contracts_examined = excluded.contracts_examined,
                is_fallback = excluded.is_fallback;
            """;
        command.Parameters.AddWithValue("$runId", prompt.RunId.ToString("N"));
        command.Parameters.AddWithValue("$order", prompt.DisplayOrder);
        command.Parameters.AddWithValue("$text", prompt.Text.Trim());
        command.Parameters.AddWithValue("$pivot", prompt.RandomPivot);
        command.Parameters.AddWithValue("$layer", DbValue(prompt.Cursor?.GeographicLayer));
        command.Parameters.AddWithValue("$group", DbValue(prompt.Cursor?.GroupRank));
        command.Parameters.AddWithValue("$band", DbValue(prompt.Cursor?.RotationBand));
        command.Parameters.AddWithValue("$random", DbValue(prompt.Cursor?.RandomOrderKey));
        command.Parameters.AddWithValue("$pncpId", DbValue(prompt.Cursor?.PncpId));
        command.Parameters.AddWithValue("$exhausted", prompt.CandidateSetExhausted ? 1 : 0);
        command.Parameters.AddWithValue("$examined", Math.Max(0, prompt.ContractsExamined));
        command.Parameters.AddWithValue("$fallback", prompt.IsFallback ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContractSearchCheckpoint>> GetProcessedContractsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT contract_id, prompt_order, processed_at, matched_items, revealed_prices
              FROM quotation_processed_contracts
             WHERE run_id = $runId
             ORDER BY processed_at, contract_id;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString("N"));
        var result = new List<ContractSearchCheckpoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ContractSearchCheckpoint
            {
                RunId = runId,
                ContractId = reader.GetString(0),
                PromptOrder = reader.GetInt32(1),
                ProcessedAt = ParseDateTime(reader.GetString(2)),
                MatchedItems = reader.GetInt32(3),
                RevealedPrices = reader.GetInt32(4)
            });
        }

        return result;
    }

    public async Task SaveProcessedContractAsync(
        ContractSearchCheckpoint checkpoint,
        TimedQuotationProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(progress);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO quotation_processed_contracts(
                    run_id, contract_id, prompt_order, processed_at, matched_items, revealed_prices)
                VALUES($runId, $contractId, $promptOrder, $processedAt, $matched, $revealed)
                ON CONFLICT(run_id, contract_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$runId", checkpoint.RunId.ToString("N"));
            insert.Parameters.AddWithValue("$contractId", checkpoint.ContractId);
            insert.Parameters.AddWithValue("$promptOrder", checkpoint.PromptOrder);
            insert.Parameters.AddWithValue("$processedAt", FormatDateTime(checkpoint.ProcessedAt));
            insert.Parameters.AddWithValue("$matched", checkpoint.MatchedItems);
            insert.Parameters.AddWithValue("$revealed", checkpoint.RevealedPrices);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE quotation_automation_runs
                   SET unique_contracts_processed = $contracts,
                       matched_items = $matched,
                       revealed_prices = $revealed,
                       item_list_cache_hits = $cacheHits,
                       item_list_api_calls = $listCalls,
                       item_result_api_calls = $resultCalls,
                       failed_calls = $failed,
                       consecutive_no_results = $noResults,
                       updated_at = $updated
                 WHERE id = $runId;
                """;
            update.Parameters.AddWithValue("$contracts", progress.UniqueContractsProcessed);
            update.Parameters.AddWithValue("$matched", progress.MatchedItems);
            update.Parameters.AddWithValue("$revealed", progress.RevealedPrices);
            update.Parameters.AddWithValue("$cacheHits", progress.ItemListsFromCache);
            update.Parameters.AddWithValue("$listCalls", progress.ItemListsFromApi);
            update.Parameters.AddWithValue("$resultCalls", progress.ItemResultCalls);
            update.Parameters.AddWithValue("$failed", progress.FailedCalls);
            update.Parameters.AddWithValue("$noResults", progress.ContractsWithoutResult);
            update.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$runId", checkpoint.RunId.ToString("N"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItemSearchPromptSet>> GetPendingPromptRevalidationsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.line_id, p.version, p.restrictive_text, p.intermediate_text, p.broad_text,
                   p.origin, p.validation_state, p.active_level, p.contracts_at_level,
                   p.matched_items, p.revealed_prices, p.updated_at
              FROM quotation_line_search_prompts p
              JOIN quotation_lines l ON l.id = p.line_id
              LEFT JOIN quotation_prompt_revalidations r
                ON r.run_id = l.automation_run_id
               AND r.line_id = p.line_id
               AND r.prompt_version = p.version
             WHERE l.automation_run_id = $runId
               AND p.is_current = 1
               AND r.line_id IS NULL
             ORDER BY l.display_order;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString("N"));
        var result = new List<ItemSearchPromptSet>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadPromptSet(
                reader,
                Guid.ParseExact(reader.GetString(0), "N"),
                1));
        }

        return result;
    }

    public async Task MarkPromptRevalidatedAsync(
        Guid runId,
        Guid lineId,
        int promptVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO quotation_prompt_revalidations(
                run_id, line_id, prompt_version, completed_at)
            VALUES($runId, $lineId, $version, $completed);
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString("N"));
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        command.Parameters.AddWithValue("$version", promptVersion);
        command.Parameters.AddWithValue("$completed", FormatDateTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TouchLineAndProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid lineId,
        bool clearSelection,
        CancellationToken cancellationToken)
    {
        var now = FormatDateTime(DateTimeOffset.UtcNow);
        await using (var line = connection.CreateCommand())
        {
            line.Transaction = transaction;
            line.CommandText = clearSelection
                ? "UPDATE quotation_lines SET selection_confirmed = 0 WHERE id = $lineId;"
                : "SELECT 1;";
            if (clearSelection)
            {
                line.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
            }

            await line.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var project = connection.CreateCommand();
        project.Transaction = transaction;
        project.CommandText = """
            UPDATE quotation_projects
               SET updated_at = $updated
             WHERE id = (SELECT project_id FROM quotation_lines WHERE id = $lineId);
            """;
        project.Parameters.AddWithValue("$updated", now);
        project.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                explanation, state, state_reason, duplicate_of_reference_id,
                prompt_match_level, matched_search_text, source_kind,
                supplier_municipality, supplier_uf)
            VALUES($id, $lineId, $contractId, $itemNumber, $resultSequence, $supplierName,
                   $supplierTaxId, $supplierType, $homologatedQuantity, $unitPrice, $resultDate,
                   $itemDescription, $itemAdditional, $itemUnit, $itemRequestedQuantity,
                   $itemCategory, $ncmNbsCode, $ncmNbsDescription, $catalogCode, $catalogName,
                   $catalogCategory, $organization, $municipality, $uf, $distance, $publicationDate,
                   $portalUrl, $descriptionScore, $unitScore, $quantityScore, $proximityScore,
                   $recencyScore, $explanation, $state, $stateReason, $duplicateOf,
                   $promptLevel, $matchedSearchText, $sourceKind,
                   $supplierMunicipality, $supplierUf)
            ON CONFLICT(line_id, id) DO UPDATE SET
                contract_id = excluded.contract_id,
                item_number = excluded.item_number,
                result_sequence = excluded.result_sequence,
                supplier_name = excluded.supplier_name,
                supplier_tax_id = excluded.supplier_tax_id,
                supplier_type = excluded.supplier_type,
                homologated_quantity_scaled = excluded.homologated_quantity_scaled,
                unit_price_scaled = excluded.unit_price_scaled,
                result_date = excluded.result_date,
                item_description = excluded.item_description,
                item_additional_information = excluded.item_additional_information,
                item_unit = excluded.item_unit,
                item_requested_quantity_scaled = excluded.item_requested_quantity_scaled,
                item_category = excluded.item_category,
                ncm_nbs_code = excluded.ncm_nbs_code,
                ncm_nbs_description = excluded.ncm_nbs_description,
                catalog_code = excluded.catalog_code,
                catalog_name = excluded.catalog_name,
                catalog_category = excluded.catalog_category,
                organization = excluded.organization,
                municipality = excluded.municipality,
                uf = excluded.uf,
                distance_ribeirao_km = excluded.distance_ribeirao_km,
                publication_date = excluded.publication_date,
                portal_url = excluded.portal_url,
                description_score_scaled = excluded.description_score_scaled,
                unit_score_scaled = excluded.unit_score_scaled,
                quantity_score_scaled = excluded.quantity_score_scaled,
                proximity_score_scaled = excluded.proximity_score_scaled,
                recency_score_scaled = excluded.recency_score_scaled,
                explanation = excluded.explanation,
                state = excluded.state,
                state_reason = excluded.state_reason,
                duplicate_of_reference_id = excluded.duplicate_of_reference_id,
                prompt_match_level = excluded.prompt_match_level,
                matched_search_text = excluded.matched_search_text,
                source_kind = excluded.source_kind,
                supplier_municipality = excluded.supplier_municipality,
                supplier_uf = excluded.supplier_uf;
            """;
        foreach (var name in new[]
                 {
                     "$id", "$lineId", "$contractId", "$itemNumber", "$resultSequence", "$supplierName",
                     "$supplierTaxId", "$supplierType", "$homologatedQuantity", "$unitPrice", "$resultDate",
                     "$itemDescription", "$itemAdditional", "$itemUnit", "$itemRequestedQuantity", "$itemCategory",
                     "$ncmNbsCode", "$ncmNbsDescription", "$catalogCode", "$catalogName", "$catalogCategory",
                     "$organization", "$municipality", "$uf", "$distance", "$publicationDate", "$portalUrl",
                     "$descriptionScore", "$unitScore", "$quantityScore", "$proximityScore", "$recencyScore",
                     "$explanation", "$state", "$stateReason", "$duplicateOf",
                     "$promptLevel", "$matchedSearchText", "$sourceKind",
                     "$supplierMunicipality", "$supplierUf"
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
            command.Parameters["$supplierMunicipality"].Value = reference.SupplierMunicipality;
            command.Parameters["$supplierUf"].Value = reference.SupplierUf;
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
            command.Parameters["$promptLevel"].Value = DbValue(
                reference.MatchedPromptLevel is null ? null : (int)reference.MatchedPromptLevel.Value);
            command.Parameters["$matchedSearchText"].Value = reference.MatchedSearchText;
            command.Parameters["$sourceKind"].Value = (int)reference.Source;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertEvidenceAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidenceImageDescriptor? descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor is null)
        {
            return;
        }

        if (descriptor.Sha256.Length != 64 ||
            descriptor.RelativePath.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(descriptor.RelativePath))
        {
            throw new InvalidDataException("O descritor da evidência contém caminho ou hash inválido.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quotation_internet_evidence_assets(
                sha256, relative_path, mime_type, byte_length,
                pixel_width, pixel_height, created_at)
            VALUES($sha, $path, $mime, $length, $width, $height, $created)
            ON CONFLICT(sha256) DO UPDATE SET
                relative_path = excluded.relative_path,
                mime_type = excluded.mime_type,
                byte_length = excluded.byte_length,
                pixel_width = excluded.pixel_width,
                pixel_height = excluded.pixel_height;
            """;
        command.Parameters.AddWithValue("$sha", descriptor.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue("$path", descriptor.RelativePath.Replace('\\', '/'));
        command.Parameters.AddWithValue("$mime", descriptor.MimeType);
        command.Parameters.AddWithValue("$length", descriptor.ByteLength);
        command.Parameters.AddWithValue("$width", descriptor.PixelWidth);
        command.Parameters.AddWithValue("$height", descriptor.PixelHeight);
        command.Parameters.AddWithValue("$created", FormatDateTime(descriptor.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task TouchLineProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid lineId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE quotation_projects
               SET updated_at = $updated
             WHERE id = (SELECT project_id FROM quotation_lines WHERE id = $lineId);
            """;
        command.Parameters.AddWithValue("$updated", FormatDateTime(updatedAt));
        command.Parameters.AddWithValue("$lineId", lineId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("O item da cotação não existe mais.");
        }
    }

    private static QuotationProject ReadProject(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        ParseDateTime(reader.GetString(2)),
        ParseDateTime(reader.GetString(3)));

    private static QuotationAutomationRun ReadAutomationRun(SqliteDataReader reader)
    {
        var kind = (SearchGeoFilterKind)reader.GetInt32(3);
        var uf = reader.IsDBNull(4) ? null : reader.GetString(4);
        var filter = kind switch
        {
            SearchGeoFilterKind.All => SearchGeoFilter.All,
            SearchGeoFilterKind.Southeast => SearchGeoFilter.Southeast,
            SearchGeoFilterKind.NearRibeirao => SearchGeoFilter.NearRibeirao,
            SearchGeoFilterKind.State when uf is not null => SearchGeoFilter.State(uf),
            _ => SearchGeoFilter.All
        };
        return new QuotationAutomationRun
        {
            Id = Guid.ParseExact(reader.GetString(0), "N"),
            ProjectId = Guid.ParseExact(reader.GetString(1), "N"),
            OutputPath = reader.GetString(2),
            GeoFilter = filter,
            StartDate = DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            EndDate = DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            State = (QuotationAutomationRunState)reader.GetInt32(7),
            Message = reader.GetString(8),
            CreatedAt = ParseDateTime(reader.GetString(9)),
            UpdatedAt = ParseDateTime(reader.GetString(10)),
            Mode = (QuotationAutomationMode)reader.GetInt32(11),
            TimeBudget = TimeSpan.FromSeconds(reader.GetInt64(12)),
            ActiveElapsed = TimeSpan.FromSeconds(reader.GetInt64(13)),
            SourceDraftId = reader.IsDBNull(14) ? null : Guid.ParseExact(reader.GetString(14), "N"),
            SourcePdfSha256 = reader.GetString(15),
            StrategyVersion = reader.GetInt32(16),
            UniqueContractsProcessed = reader.GetInt32(17),
            MatchedItems = reader.GetInt32(18),
            RevealedPrices = reader.GetInt32(19),
            ItemListCacheHits = reader.GetInt32(20),
            ItemListApiCalls = reader.GetInt32(21),
            ItemResultApiCalls = reader.GetInt32(22),
            FailedCalls = reader.GetInt32(23),
            ConsecutiveContractsWithoutResult = reader.GetInt32(24)
        };
    }

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
        SelectionConfirmed = reader.GetInt64(15) == 1,
        SearchText = reader.GetString(16),
        RequestedBatchCount = reader.GetInt32(17),
        DisplayOrder = reader.GetInt32(18),
        AutomationRunId = reader.IsDBNull(19) ? null : Guid.ParseExact(reader.GetString(19), "N"),
        AutomationState = (QuotationAutomationItemState)reader.GetInt32(20),
        AutomationMessage = reader.GetString(21),
        RequestedBasketSize = reader.GetInt32(22),
        EstimatedUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 23)),
        EstimatedTotalPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 24)),
        UseEstimatedPrice = reader.GetInt64(25) == 1,
        EstimateStage = (EstimateResolutionStage)reader.GetInt32(26),
        SearchCheckpoint = new ItemSearchCheckpoint
        {
            RandomPivot = reader.GetInt64(27),
            Cursor = reader.IsDBNull(32)
                ? null
                : new ItemCandidateCursor(
                    reader.GetInt32(28),
                    reader.GetInt32(29),
                    reader.GetInt32(30),
                    reader.GetInt64(31),
                    reader.GetString(32)),
            ContractsExamined = reader.GetInt32(33),
            BatchesCompleted = reader.GetInt32(34),
            CandidateSetExhausted = reader.GetInt64(35) == 1,
            EstimateStage = (EstimateResolutionStage)reader.GetInt32(26)
        },
        PromptSet = reader.IsDBNull(36)
            ? null
            : ReadPromptSet(reader, Guid.ParseExact(reader.GetString(0), "N"), 36),
        DisplayName = reader.GetString(47),
        CatalogSelection = reader.IsDBNull(48)
            ? null
            : new QuotationCatalogSelection
            {
                Kind = (CatalogKind)reader.GetInt32(48),
                Code = reader.GetString(49),
                Description = reader.GetString(50),
                SelectedAt = ParseDateTime(reader.GetString(51)),
                IsActive = reader.GetInt64(52) == 1
            }
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
        DuplicateOfReferenceId = reader.IsDBNull(35) ? null : reader.GetString(35),
        MatchedPromptLevel = reader.IsDBNull(36) ? null : (PromptMatchLevel)reader.GetInt32(36),
        MatchedSearchText = reader.GetString(37),
        Source = (QuotationReferenceSource)reader.GetInt32(38),
        SupplierMunicipality = reader.GetString(39),
        SupplierUf = reader.GetString(40)
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

        if (input.RequestedBasketSize is < 3 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "O número de preços da cesta automática deve estar entre 3 e 10.");
        }

        input.Weights.Validate();
    }

    private static DateTimeOffset ParseDateTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static QuotationItemSearchWorkspace ReadItemSearchWorkspace(SqliteDataReader reader)
    {
        var geoKind = (SearchGeoFilterKind)reader.GetInt32(3);
        var geoFilter = geoKind switch
        {
            SearchGeoFilterKind.All => SearchGeoFilter.All,
            SearchGeoFilterKind.Southeast => SearchGeoFilter.Southeast,
            SearchGeoFilterKind.NearRibeirao => SearchGeoFilter.NearRibeirao,
            SearchGeoFilterKind.State when !reader.IsDBNull(4) =>
                SearchGeoFilter.State(reader.GetString(4)),
            _ => SearchGeoFilter.All
        };
        var cursor = reader.IsDBNull(12)
            ? null
            : new ItemCandidateCursor(
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt64(15),
                reader.GetString(16));
        return new QuotationItemSearchWorkspace
        {
            LineId = Guid.ParseExact(reader.GetString(0), "N"),
            Slot = (ItemSearchPromptSlot)reader.GetInt32(1),
            SearchText = reader.GetString(2),
            GeoFilter = geoFilter,
            StartDate = DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            EndDate = DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Sort = (SearchSort)reader.GetInt32(7),
            MinimumUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 8)),
            MaximumUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 9)),
            BatchCount = Math.Clamp(reader.GetInt32(10), 1, 100),
            Checkpoint = new QuotationItemSearchCheckpoint
            {
                RandomPivot = reader.GetInt64(11),
                Cursor = cursor,
                ContractsExamined = reader.GetInt32(17),
                BatchesCompleted = reader.GetInt32(18),
                CandidateSetExhausted = reader.GetInt64(19) == 1
            },
            MatchedItems = reader.GetInt32(20),
            RevealedPrices = reader.GetInt32(21),
            ItemListsFromCache = reader.GetInt32(22),
            ItemListsFromApi = reader.GetInt32(23),
            ItemResultApiCalls = reader.GetInt32(24),
            FailedCalls = reader.GetInt32(25),
            StatusMessage = reader.GetString(26),
            UpdatedAt = ParseDateTime(reader.GetString(27))
        };
    }

    private static void SetWorkspaceCommand(
        SqliteCommand command,
        QuotationItemSearchWorkspace workspace)
    {
        if (workspace.StartDate > workspace.EndDate)
        {
            throw new ArgumentException("O período da pesquisa detalhada é inválido.", nameof(workspace));
        }

        if (workspace.BatchCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspace),
                "A pesquisa detalhada aceita de 1 a 100 lotes.");
        }

        if (workspace.MinimumUnitPrice < 0 ||
            workspace.MaximumUnitPrice < 0 ||
            workspace.MinimumUnitPrice is not null &&
            workspace.MaximumUnitPrice is not null &&
            workspace.MinimumUnitPrice > workspace.MaximumUnitPrice)
        {
            throw new ArgumentException("A faixa de preços da pesquisa detalhada é inválida.", nameof(workspace));
        }

        command.CommandText = """
            INSERT INTO quotation_item_search_workspaces(
                line_id, prompt_slot, search_text, geo_filter_kind, geo_filter_uf,
                start_date, end_date, sort_kind, minimum_unit_price_scaled,
                maximum_unit_price_scaled, batch_count, random_pivot,
                cursor_geo_layer, cursor_group_rank, cursor_rotation_band,
                cursor_random_key, cursor_pncp_id, contracts_examined,
                batches_completed, candidate_set_exhausted, matched_items,
                revealed_prices, item_lists_from_cache, item_lists_from_api,
                item_result_api_calls, failed_calls, status_message, updated_at)
            VALUES(
                $lineId, $slot, $text, $geoKind, $geoUf, $start, $end, $sort,
                $minimum, $maximum, $batches, $pivot, $cursorLayer, $cursorGroup,
                $cursorBand, $cursorRandom, $cursorPncp, $examined, $completed,
                $exhausted, $matched, $revealed, $cacheLists, $apiLists,
                $resultCalls, $failed, $message, $updated)
            ON CONFLICT(line_id, prompt_slot) DO UPDATE SET
                search_text = excluded.search_text,
                geo_filter_kind = excluded.geo_filter_kind,
                geo_filter_uf = excluded.geo_filter_uf,
                start_date = excluded.start_date,
                end_date = excluded.end_date,
                sort_kind = excluded.sort_kind,
                minimum_unit_price_scaled = excluded.minimum_unit_price_scaled,
                maximum_unit_price_scaled = excluded.maximum_unit_price_scaled,
                batch_count = excluded.batch_count,
                random_pivot = excluded.random_pivot,
                cursor_geo_layer = excluded.cursor_geo_layer,
                cursor_group_rank = excluded.cursor_group_rank,
                cursor_rotation_band = excluded.cursor_rotation_band,
                cursor_random_key = excluded.cursor_random_key,
                cursor_pncp_id = excluded.cursor_pncp_id,
                contracts_examined = excluded.contracts_examined,
                batches_completed = excluded.batches_completed,
                candidate_set_exhausted = excluded.candidate_set_exhausted,
                matched_items = excluded.matched_items,
                revealed_prices = excluded.revealed_prices,
                item_lists_from_cache = excluded.item_lists_from_cache,
                item_lists_from_api = excluded.item_lists_from_api,
                item_result_api_calls = excluded.item_result_api_calls,
                failed_calls = excluded.failed_calls,
                status_message = excluded.status_message,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$lineId", workspace.LineId.ToString("N"));
        command.Parameters.AddWithValue("$slot", (int)workspace.Slot);
        command.Parameters.AddWithValue("$text", workspace.SearchText);
        command.Parameters.AddWithValue("$geoKind", (int)workspace.GeoFilter.Kind);
        command.Parameters.AddWithValue("$geoUf", DbValue(workspace.GeoFilter.Uf));
        command.Parameters.AddWithValue("$start", workspace.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", workspace.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sort", (int)workspace.Sort);
        command.Parameters.AddWithValue("$minimum", DbValue(DecimalScale.ToScaled(workspace.MinimumUnitPrice)));
        command.Parameters.AddWithValue("$maximum", DbValue(DecimalScale.ToScaled(workspace.MaximumUnitPrice)));
        command.Parameters.AddWithValue("$batches", workspace.BatchCount);
        command.Parameters.AddWithValue("$pivot", workspace.Checkpoint.RandomPivot);
        command.Parameters.AddWithValue("$cursorLayer", DbValue(workspace.Checkpoint.Cursor?.GeographicLayer));
        command.Parameters.AddWithValue("$cursorGroup", DbValue(workspace.Checkpoint.Cursor?.GroupRank));
        command.Parameters.AddWithValue("$cursorBand", DbValue(workspace.Checkpoint.Cursor?.RotationBand));
        command.Parameters.AddWithValue("$cursorRandom", DbValue(workspace.Checkpoint.Cursor?.RandomOrderKey));
        command.Parameters.AddWithValue("$cursorPncp", DbValue(workspace.Checkpoint.Cursor?.PncpId));
        command.Parameters.AddWithValue("$examined", Math.Max(0, workspace.Checkpoint.ContractsExamined));
        command.Parameters.AddWithValue("$completed", Math.Max(0, workspace.Checkpoint.BatchesCompleted));
        command.Parameters.AddWithValue("$exhausted", workspace.Checkpoint.CandidateSetExhausted ? 1 : 0);
        command.Parameters.AddWithValue("$matched", Math.Max(0, workspace.MatchedItems));
        command.Parameters.AddWithValue("$revealed", Math.Max(0, workspace.RevealedPrices));
        command.Parameters.AddWithValue("$cacheLists", Math.Max(0, workspace.ItemListsFromCache));
        command.Parameters.AddWithValue("$apiLists", Math.Max(0, workspace.ItemListsFromApi));
        command.Parameters.AddWithValue("$resultCalls", Math.Max(0, workspace.ItemResultApiCalls));
        command.Parameters.AddWithValue("$failed", Math.Max(0, workspace.FailedCalls));
        command.Parameters.AddWithValue("$message", workspace.StatusMessage);
        command.Parameters.AddWithValue("$updated", FormatDateTime(workspace.UpdatedAt));
    }

    private static DateOnly? ParseDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || !DateOnly.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? null
            : date;

    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static string FormatDateTime(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static string NormalizeTaxId(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static EvidenceImageDescriptor? ReadEvidenceImage(
        SqliteDataReader reader,
        int offset) =>
        reader.IsDBNull(offset)
            ? null
            : new EvidenceImageDescriptor
            {
                Sha256 = reader.GetString(offset),
                RelativePath = reader.GetString(offset + 1),
                MimeType = reader.GetString(offset + 2),
                ByteLength = reader.GetInt64(offset + 3),
                PixelWidth = reader.GetInt32(offset + 4),
                PixelHeight = reader.GetInt32(offset + 5),
                CreatedAt = ParseDateTime(reader.GetString(offset + 6))
            };

    private static ItemSearchPromptSet ReadPromptSet(
        SqliteDataReader reader,
        Guid lineId,
        int offset = 0) =>
        new()
        {
            LineId = lineId,
            Version = reader.GetInt32(offset),
            RestrictiveText = reader.GetString(offset + 1),
            IntermediateText = reader.GetString(offset + 2),
            BroadText = reader.GetString(offset + 3),
            Origin = (SearchPromptOrigin)reader.GetInt32(offset + 4),
            ValidationState = (SearchPromptValidationState)reader.GetInt32(offset + 5),
            ActiveLevel = (PromptMatchLevel)reader.GetInt32(offset + 6),
            ContractsAtActiveLevel = reader.GetInt32(offset + 7),
            MatchedItems = reader.GetInt32(offset + 8),
            RevealedPrices = reader.GetInt32(offset + 9),
            UpdatedAt = ParseDateTime(reader.GetString(offset + 10))
        };

    private static string ExtractContractFallbackPrompt(string value)
    {
        var expression = PNCPKing.Core.Search.SearchText.Parse(value);
        return string.Join(
            " ",
            expression.PositiveText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(3));
    }

    private const string ReferenceSelectSql = """
        SELECT id, line_id, contract_id, item_number, result_sequence, supplier_name,
               supplier_tax_id, supplier_type, homologated_quantity_scaled, unit_price_scaled,
               result_date, item_description, item_additional_information, item_unit,
               item_requested_quantity_scaled, item_category, ncm_nbs_code, ncm_nbs_description,
               catalog_code, catalog_name, catalog_category, organization, municipality, uf,
               distance_ribeirao_km, publication_date, portal_url, description_score_scaled,
               unit_score_scaled, quantity_score_scaled, proximity_score_scaled, recency_score_scaled,
               explanation, state, state_reason, duplicate_of_reference_id,
               prompt_match_level, matched_search_text, source_kind,
               supplier_municipality, supplier_uf
          FROM quotation_references
        """;
}
