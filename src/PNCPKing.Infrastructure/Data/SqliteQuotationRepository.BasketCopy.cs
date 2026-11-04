using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteQuotationRepository
{
    public async Task<Guid> SaveBasketReferenceCopyAsync(QuotationLine expectedLine, QuotationBasket source,
        QuotationBasket destination, string referenceId, string automaticCopyName,
        CancellationToken cancellationToken = default)
    {
        if (source.Key == destination.Key)
            throw new InvalidOperationException("Escolha outra cesta do mesmo item.");
        var copied = source.PriceEntries.SingleOrDefault(entry => entry.Reference.Id == referenceId)
            ?? throw new InvalidOperationException("O preço não pertence à cesta de origem.");
        if (copied.Reference.LineId != expectedLine.Id || source.References.All(reference => reference.Id != referenceId))
            throw new InvalidOperationException("O preço não pertence ao item e à cesta de origem.");
        if (destination.References.Any(reference => reference.Id == referenceId))
            throw new InvalidOperationException("Esse preço já está na cesta de destino.");
        if (destination.References.Count >= 10)
            throw new InvalidOperationException("A cesta de destino já contém dez preços.");
        if (source.References.Concat(destination.References).Any(reference => reference.LineId != expectedLine.Id))
            throw new InvalidOperationException("Só é possível copiar preços entre cestas do mesmo item.");
        var entries = destination.PriceEntries.Append(copied).ToArray();
        if (entries.Length != destination.References.Count + 1 ||
            entries.Select(value => value.Reference.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidOperationException("Os preços da cesta mudaram. Atualize o item.");
        if (entries.Where(value => value.Reference.Source == QuotationReferenceSource.PncpIncisoII &&
                                  !string.IsNullOrWhiteSpace(value.Reference.SupplierTaxId) &&
                                  !string.IsNullOrWhiteSpace(value.Reference.ContractId))
            .GroupBy(value => CopyOrigin(value.Reference), StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("A cesta de destino repetiria a mesma empresa na mesma contratação PNCP.");
        foreach (var entry in entries)
        {
            _ = ScaleConversionFactor(entry.ConversionFactor);
            if (entry.PriceDivisor is { } divisor)
            {
                QuotationMoney.ValidatePriceDivisor(divisor);
                if (entry.ConversionFactor != 1m)
                    throw new InvalidOperationException("Uma referência não pode combinar divisão e multiplicação.");
            }
        }

        await using var writer = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadAiGuideLineAsync(connection, transaction, expectedLine, cancellationToken).ConfigureAwait(false);
        if (current is null || current.SampleVersion != expectedLine.SampleVersion ||
            QuotationAiPackage.SpecificationHash(current) != QuotationAiPackage.SpecificationHash(expectedLine))
            throw new InvalidOperationException("O item mudou. Atualize as cestas antes de copiar o preço.");
        var hasDivisors = await HasPriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CheckCopyBasketSnapshotAsync(connection, transaction, source, expectedLine.Id, hasDivisors, cancellationToken).ConfigureAwait(false);
        await CheckCopyBasketSnapshotAsync(connection, transaction, destination, expectedLine.Id, hasDivisors, cancellationToken).ConfigureAwait(false);
        foreach (var reference in source.References.Concat(destination.References).DistinctBy(value => value.Id))
        {
            await using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = ReferenceSelectSql + " WHERE line_id = $line AND id = $id;";
            check.Parameters.AddWithValue("$line", expectedLine.Id.ToString("N"));
            check.Parameters.AddWithValue("$id", reference.Id);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Uma referência foi removida do item.");
            var stored = ReadReference(reader);
            if (stored.UnitPrice != reference.UnitPrice || CopyOrigin(stored) != CopyOrigin(reference) ||
                stored.ItemDescription != reference.ItemDescription || stored.ItemUnit != reference.ItemUnit ||
                stored.ItemAdditionalInformation != reference.ItemAdditionalInformation)
                throw new InvalidOperationException("Os dados de um preço mudaram. Atualize as cestas.");
        }

        var destinationId = destination.IsManual ? destination.ManualBasketId!.Value : Guid.NewGuid();
        var now = FormatDateTime(DateTimeOffset.UtcNow);
        await using (var basket = connection.CreateCommand())
        {
            basket.Transaction = transaction;
            basket.CommandText = destination.IsManual
                ? "UPDATE quotation_manual_baskets SET updated_at = $now WHERE id = $id;"
                : """
                  INSERT INTO quotation_manual_baskets(id,line_id,name,display_order,created_at,updated_at,calculation_method)
                  VALUES($id,$line,$name,COALESCE((SELECT MAX(display_order)+1 FROM quotation_manual_baskets WHERE line_id=$line),0),$now,$now,$method);
                  """;
            basket.Parameters.AddWithValue("$id", destinationId.ToString("N"));
            basket.Parameters.AddWithValue("$now", now);
            if (!destination.IsManual)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(automaticCopyName);
                basket.Parameters.AddWithValue("$line", expectedLine.Id.ToString("N"));
                basket.Parameters.AddWithValue("$name", automaticCopyName.Trim());
                basket.Parameters.AddWithValue("$method", (int)destination.AggregationMethod);
            }
            await basket.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var inserted = destination.IsManual ? new[] { copied } : entries;
        if (inserted.Any(entry => entry.PriceDivisor is not null))
            await EnsurePriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var entry in inserted)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO quotation_manual_basket_references(basket_id,line_id,reference_id,display_order,conversion_factor_millionths)
                VALUES($basket,$line,$reference,COALESCE((SELECT MAX(display_order)+1 FROM quotation_manual_basket_references WHERE basket_id=$basket),0),$factor);
                """;
            insert.Parameters.AddWithValue("$basket", destinationId.ToString("N"));
            insert.Parameters.AddWithValue("$line", expectedLine.Id.ToString("N"));
            insert.Parameters.AddWithValue("$reference", entry.Reference.Id);
            insert.Parameters.AddWithValue("$factor", ScaleConversionFactor(entry.ConversionFactor));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (entry.PriceDivisor is not { } divisor) continue;
            insert.CommandText = "INSERT INTO quotation_manual_price_divisors(basket_id,reference_id,price_divisor_millionths) VALUES($basket,$reference,$divisor);";
            insert.Parameters.AddWithValue("$divisor", checked((long)(divisor * 1_000_000m)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = """
                UPDATE quotation_lines SET selection_confirmed = CASE WHEN selected_basket_key=$key THEN 0 ELSE selection_confirmed END WHERE id=$line;
                UPDATE quotation_projects SET updated_at=$now WHERE id=$project;
                """;
            touch.Parameters.AddWithValue("$key", $"manual:{destinationId:N}");
            touch.Parameters.AddWithValue("$line", expectedLine.Id.ToString("N"));
            touch.Parameters.AddWithValue("$project", expectedLine.ProjectId.ToString("N"));
            touch.Parameters.AddWithValue("$now", now);
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return destinationId;
    }

    private static string CopyOrigin(QuotationReference reference) =>
        string.Concat(reference.SupplierTaxId.Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant() + "|" + reference.ContractId;

    private static async Task CheckCopyBasketSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction,
        QuotationBasket basket, Guid lineId, bool hasDivisors, CancellationToken token)
    {
        if (!basket.IsManual) return;
        if (basket.ManualBasketId is not { } id) throw new InvalidOperationException("Cesta manual sem identificador.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, calculation_method FROM quotation_manual_baskets WHERE id=$id AND line_id=$line;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$line", lineId.ToString("N"));
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != basket.Name ||
                reader.GetInt32(1) != (int)basket.AggregationMethod)
                throw new InvalidOperationException("A cesta mudou. Atualize o item antes de copiar.");
        command.CommandText = $"""
            SELECT m.reference_id,m.conversion_factor_millionths,{(hasDivisors ? "d.price_divisor_millionths" : "NULL")}
            FROM quotation_manual_basket_references m
            {(hasDivisors ? "LEFT JOIN quotation_manual_price_divisors d ON d.basket_id=m.basket_id AND d.reference_id=m.reference_id" : "")}
            WHERE m.basket_id=$id;
            """;
        var found = 0;
        var entries = basket.PriceEntries.ToDictionary(value => value.Reference.Id, StringComparer.Ordinal);
        await using var members = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await members.ReadAsync(token).ConfigureAwait(false))
        {
            found++;
            if (!entries.TryGetValue(members.GetString(0), out var entry) ||
                members.GetInt64(1) != ScaleConversionFactor(entry.ConversionFactor) ||
                (members.IsDBNull(2) ? (decimal?)null : members.GetInt64(2) / 1_000_000m) != entry.PriceDivisor)
                throw new InvalidOperationException("Os preços ou conversões da cesta mudaram. Atualize o item.");
        }
        if (found != entries.Count) throw new InvalidOperationException("Os preços da cesta mudaram. Atualize o item.");
    }
}
