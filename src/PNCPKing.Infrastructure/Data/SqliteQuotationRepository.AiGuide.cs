using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteQuotationRepository
{
    // A whole item commits once: a failed/cancelled import cannot leave half a basket.
    // This adds only rows to existing quotation tables; it never changes the price database schema.
    public async Task<bool> SaveAiGuideBasketAsync(
        QuotationAiBasketApplication application, CancellationToken cancellationToken = default)
    {
        var expected = application.ExpectedLine;
        if (application.Selections.Count is < 3 or > 10 ||
            application.Selections.Select(value => value.Reference.Id).Distinct(StringComparer.Ordinal).Count() !=
            application.Selections.Count)
            throw new InvalidDataException("A cesta guiada exige de 3 a 10 referências distintas.");
        foreach (var selection in application.Selections)
        {
            if (selection.Reference.LineId != expected.Id)
                throw new InvalidDataException("A referência não pertence ao item do guia.");
            _ = ScaleConversionFactor(selection.ConversionFactor);
            if (selection.PriceDivisor is { } divisor)
            {
                QuotationMoney.ValidatePriceDivisor(divisor);
                if (selection.ConversionFactor != 1m)
                    throw new InvalidDataException("A divisão de preços exige fator 1.");
            }
        }

        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadAiGuideLineAsync(connection, transaction, expected, cancellationToken).ConfigureAwait(false);
        if (current is null || QuotationAiPackage.SpecificationHash(current) != QuotationAiPackage.SpecificationHash(expected) ||
            current.SampleVersion != expected.SampleVersion)
            throw new InvalidDataException("O item mudou desde a prévia. Gere uma nova prévia do guia.");
        await using (var project = connection.CreateCommand())
        {
            project.Transaction = transaction;
            project.CommandText = "SELECT is_medication FROM quotation_projects WHERE id = $id;";
            project.Parameters.AddWithValue("$id", expected.ProjectId.ToString("N"));
            var value = await project.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || (Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0) != application.IsMedication)
                throw new InvalidDataException("A precisão da cotação mudou desde a coleta.");
        }

        var newReferences = new List<QuotationReference>();
        foreach (var selection in application.Selections)
        {
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = ReferenceSelectSql + " WHERE line_id = $line AND id = $id;";
            existing.Parameters.AddWithValue("$line", expected.Id.ToString("N"));
            existing.Parameters.AddWithValue("$id", selection.Reference.Id);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!SameAiReference(ReadReference(reader), selection.Reference))
                    throw new InvalidDataException("Uma referência existente diverge do pacote. Os dados locais foram preservados.");
            }
            else newReferences.Add(selection.Reference);
        }

        var basketExists = false;
        await using (var basket = connection.CreateCommand())
        {
            basket.Transaction = transaction;
            basket.CommandText = "SELECT line_id, name, calculation_method FROM quotation_manual_baskets WHERE id = $id;";
            basket.Parameters.AddWithValue("$id", application.BasketId.ToString("N"));
            await using var reader = await basket.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                basketExists = true;
                if (reader.GetString(0) != expected.Id.ToString("N") || reader.GetString(1) != application.Name ||
                    reader.GetInt32(2) != (int)application.Method)
                    throw new InvalidDataException("A cesta do guia foi editada localmente. Ela não será sobrescrita.");
            }
        }
        if (basketExists)
        {
            var hasDivisors = await HasPriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using var members = connection.CreateCommand();
            members.Transaction = transaction;
            members.CommandText = $"""
                SELECT m.reference_id,m.conversion_factor_millionths,{(hasDivisors ? "d.price_divisor_millionths" : "NULL")}
                FROM quotation_manual_basket_references m
                {(hasDivisors ? "LEFT JOIN quotation_manual_price_divisors d ON d.basket_id=m.basket_id AND d.reference_id=m.reference_id" : "")}
                WHERE m.basket_id=$id ORDER BY m.display_order;
                """;
            members.Parameters.AddWithValue("$id", application.BasketId.ToString("N"));
            await using var reader = await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var index = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (index >= application.Selections.Count || reader.GetString(0) != application.Selections[index].Reference.Id ||
                    reader.GetInt64(1) != ScaleConversionFactor(application.Selections[index].ConversionFactor) ||
                    (reader.IsDBNull(2) ? (decimal?)null : reader.GetInt64(2) / 1_000_000m) != application.Selections[index].PriceDivisor)
                    throw new InvalidDataException("A cesta do guia foi editada localmente. Ela não será sobrescrita.");
                index++;
            }
            if (index != application.Selections.Count || newReferences.Count != 0)
                throw new InvalidDataException("A cesta do guia foi alterada localmente. Ela não será sobrescrita.");
            return false;
        }

        await InsertReferencesAsync(connection, transaction, expected.Id, newReferences, cancellationToken).ConfigureAwait(false);
        var now = FormatDateTime(DateTimeOffset.UtcNow);
        await using (var basket = connection.CreateCommand())
        {
            basket.Transaction = transaction;
            basket.CommandText = """
                INSERT INTO quotation_manual_baskets(id, line_id, name, display_order, created_at, updated_at, calculation_method)
                VALUES($id, $line, $name,
                    COALESCE((SELECT MAX(display_order) + 1 FROM quotation_manual_baskets WHERE line_id = $line), 0),
                    $now, $now, $method);
                """;
            basket.Parameters.AddWithValue("$id", application.BasketId.ToString("N"));
            basket.Parameters.AddWithValue("$line", expected.Id.ToString("N"));
            basket.Parameters.AddWithValue("$name", application.Name);
            basket.Parameters.AddWithValue("$now", now);
            basket.Parameters.AddWithValue("$method", (int)application.Method);
            await basket.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO quotation_manual_basket_references(basket_id,line_id,reference_id,display_order,conversion_factor_millionths)
                VALUES($basket,$line,$reference,$position,$factor);
                """;
            insert.Parameters.AddWithValue("$basket", application.BasketId.ToString("N"));
            insert.Parameters.AddWithValue("$line", expected.Id.ToString("N"));
            insert.Parameters.Add("$reference", SqliteType.Text);
            insert.Parameters.Add("$position", SqliteType.Integer);
            insert.Parameters.Add("$factor", SqliteType.Integer);
            for (var index = 0; index < application.Selections.Count; index++)
            {
                var selection = application.Selections[index];
                insert.Parameters["$reference"].Value = selection.Reference.Id;
                insert.Parameters["$position"].Value = index;
                insert.Parameters["$factor"].Value = ScaleConversionFactor(selection.ConversionFactor);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (application.Selections.Any(selection => selection.PriceDivisor is not null))
        {
            // Reuse the optional divisor storage already supported by the current release.
            await EnsurePriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using var divisor = connection.CreateCommand();
            divisor.Transaction = transaction;
            divisor.CommandText = "INSERT INTO quotation_manual_price_divisors(basket_id,reference_id,price_divisor_millionths) VALUES($basket,$reference,$divisor);";
            divisor.Parameters.AddWithValue("$basket", application.BasketId.ToString("N"));
            divisor.Parameters.Add("$reference", SqliteType.Text);
            divisor.Parameters.Add("$divisor", SqliteType.Integer);
            foreach (var selection in application.Selections.Where(selection => selection.PriceDivisor is not null))
            {
                divisor.Parameters["$reference"].Value = selection.Reference.Id;
                divisor.Parameters["$divisor"].Value = checked((long)(selection.PriceDivisor!.Value * 1_000_000m));
                await divisor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE quotation_lines SET sample_version = sample_version + 1, sampled_at = $now,
                    selected_basket_key = CASE WHEN selected_basket_key IS NULL THEN $key ELSE selected_basket_key END,
                    selection_confirmed = CASE WHEN selected_basket_key IS NULL THEN 1
                        WHEN $newReferences > 0 AND selected_basket_key NOT LIKE 'manual:%' THEN 0
                        ELSE selection_confirmed END
                WHERE id = $line;
                UPDATE quotation_projects SET updated_at = $now WHERE id = $project;
                """;
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$key", $"manual:{application.BasketId:N}");
            update.Parameters.AddWithValue("$line", expected.Id.ToString("N"));
            update.Parameters.AddWithValue("$project", expected.ProjectId.ToString("N"));
            update.Parameters.AddWithValue("$newReferences", newReferences.Count);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool SameAiReference(QuotationReference a, QuotationReference b) =>
        a.Id == b.Id && a.ContractId == b.ContractId && a.ItemNumber == b.ItemNumber &&
        a.ResultSequence == b.ResultSequence && a.UnitPrice == b.UnitPrice &&
        a.ItemDescription == b.ItemDescription && a.ItemAdditionalInformation == b.ItemAdditionalInformation &&
        a.ItemUnit == b.ItemUnit && a.SupplierTaxId == b.SupplierTaxId && a.ResultDate == b.ResultDate &&
        a.SupplierName == b.SupplierName && a.SupplierType == b.SupplierType &&
        a.SupplierMunicipality == b.SupplierMunicipality && a.SupplierUf == b.SupplierUf &&
        a.HomologatedQuantity == b.HomologatedQuantity && a.ItemRequestedQuantity == b.ItemRequestedQuantity &&
        a.ItemCategory == b.ItemCategory && a.NcmNbsCode == b.NcmNbsCode && a.NcmNbsDescription == b.NcmNbsDescription &&
        a.CatalogCode == b.CatalogCode && a.CatalogName == b.CatalogName && a.CatalogCategory == b.CatalogCategory &&
        a.Organization == b.Organization && a.Municipality == b.Municipality && a.Uf == b.Uf &&
        a.DistanceFromRibeiraoKilometers == b.DistanceFromRibeiraoKilometers &&
        a.PublicationDate == b.PublicationDate && a.PortalUrl == b.PortalUrl && a.Source == b.Source;

    private static async Task<QuotationLine?> ReadAiGuideLineAsync(SqliteConnection connection,
        SqliteTransaction transaction, QuotationLine expected, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT description,display_name,requested_quantity_scaled,requested_unit,
                minimum_unit_price_scaled,maximum_unit_price_scaled,description_weight,unit_weight,
                quantity_weight,proximity_weight,recency_weight,requested_basket_size,search_text,
                catmat_code_override,minimum_order_quantity_scaled,sample_version
            FROM quotation_lines WHERE id = $id AND project_id = $project;
            """;
        command.Parameters.AddWithValue("$id", expected.Id.ToString("N"));
        command.Parameters.AddWithValue("$project", expected.ProjectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return expected with
        {
            Description = reader.GetString(0), DisplayName = reader.GetString(1),
            RequestedQuantity = DecimalScale.FromScaled(reader.GetInt64(2))!.Value, RequestedUnit = reader.GetString(3),
            MinimumUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 4)),
            MaximumUnitPrice = DecimalScale.FromScaled(ReadNullableLong(reader, 5)),
            Weights = new(reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10)),
            RequestedBasketSize = reader.GetInt32(11), SearchText = reader.GetString(12), CatmatCodeOverride = reader.GetString(13),
            MinimumOrderQuantity = DecimalScale.FromScaled(ReadNullableLong(reader, 14)), SampleVersion = reader.GetInt32(15)
        };
    }
}
