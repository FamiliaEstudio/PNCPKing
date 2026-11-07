using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteQuotationRepository
{
    public async Task<Guid> TransferBasketReferencesAsync(
        Guid lineId, QuotationBasket source, QuotationBasket destination,
        IReadOnlyList<string> referenceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(referenceIds);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = referenceIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 || selected.Count != referenceIds.Count || selected.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Selecione preços distintos para transferir.", nameof(referenceIds));
        if (source.Key == destination.Key ||
            (source.ManualBasketId is not null && source.ManualBasketId == destination.ManualBasketId))
            throw new InvalidOperationException("Escolha uma cesta de destino diferente da origem.");

        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var hasDivisors = await HasPriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var from = await ReadEntriesAsync(source).ConfigureAwait(false);
        var to = await ReadEntriesAsync(destination).ConfigureAwait(false);
        if (selected.Any(id => !from.ContainsKey(id)))
            throw new InvalidOperationException("Um dos preços não pertence mais à cesta de origem.");
        if (selected.Any(to.ContainsKey))
            throw new InvalidOperationException("A cesta de destino já contém um dos preços selecionados.");

        // Revalidate persisted ownership inside the write transaction, including automatic snapshots.
        foreach (var id in from.Keys.Concat(to.Keys).Distinct(StringComparer.Ordinal))
        {
            await using var validate = Command("SELECT 1 FROM quotation_references WHERE id = $reference AND line_id = $line;",
                ("$reference", id), ("$line", lineId.ToString("N")));
            if (await validate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Uma referência não pertence ao item selecionado.");
        }

        if (!hasDivisors && from.Values.Concat(to.Values).Any(entry => entry.Divisor is not null))
        {
            await EnsurePriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            hasDivisors = true;
        }
        var destinationId = destination.IsManual ? destination.ManualBasketId!.Value : Guid.NewGuid();
        if (!destination.IsManual)
        {
            await CreateCopyAsync(destinationId, destination).ConfigureAwait(false);
            foreach (var entry in to)
                await InsertEntryAsync(destinationId, entry.Key, entry.Value).ConfigureAwait(false);
        }
        foreach (var id in referenceIds)
            await InsertEntryAsync(destinationId, id, from[id]).ConfigureAwait(false);

        if (source.IsManual)
        {
            foreach (var id in referenceIds)
            {
                await using var remove = Command("DELETE FROM quotation_manual_basket_references WHERE basket_id = $basket AND reference_id = $reference;",
                    ("$basket", source.ManualBasketId!.Value.ToString("N")), ("$reference", id));
                await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var empty = Command("DELETE FROM quotation_manual_baskets WHERE id = $basket AND NOT EXISTS (SELECT 1 FROM quotation_manual_basket_references WHERE basket_id = $basket);",
                ("$basket", source.ManualBasketId!.Value.ToString("N")));
            await empty.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (from.Count > selected.Count)
        {
            var sourceId = Guid.NewGuid();
            await CreateCopyAsync(sourceId, source).ConfigureAwait(false);
            foreach (var entry in from.Where(entry => !selected.Contains(entry.Key)))
                await InsertEntryAsync(sourceId, entry.Key, entry.Value).ConfigureAwait(false);
        }
        await using (var touch = Command("UPDATE quotation_manual_baskets SET updated_at = $now WHERE id = $source OR id = $destination;",
            ("$now", FormatDateTime(DateTimeOffset.UtcNow)), ("$source", source.ManualBasketId?.ToString("N") ?? ""),
            ("$destination", destinationId.ToString("N"))))
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await TouchLineAndProjectAsync(connection, transaction, lineId, clearSelection: true, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return destinationId;

        SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return command;
        }

        async Task<Dictionary<string, (long Factor, long? Divisor)>> ReadEntriesAsync(QuotationBasket basket)
        {
            var entries = new Dictionary<string, (long Factor, long? Divisor)>(StringComparer.Ordinal);
            if (basket.IsManual)
            {
                if (basket.ManualBasketId is not Guid id ||
                    await GetManualBasketLineIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false) != lineId)
                    throw new InvalidOperationException("A cesta manual não pertence ao item selecionado.");
                await using var read = Command($"""
                    SELECT m.reference_id, m.conversion_factor_millionths, {(hasDivisors ? "d.price_divisor_millionths" : "NULL")}
                    FROM quotation_manual_basket_references m
                    {(hasDivisors ? "LEFT JOIN quotation_manual_price_divisors d ON d.basket_id = m.basket_id AND d.reference_id = m.reference_id" : "")}
                    WHERE m.basket_id = $basket ORDER BY m.display_order, m.reference_id;
                    """, ("$basket", id.ToString("N")));
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    entries.Add(reader.GetString(0), (reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
            else
            {
                if (basket.ManualBasketId is not null || basket.References.Any(reference => reference.LineId != lineId))
                    throw new InvalidOperationException("A cesta não pertence ao item selecionado.");
                foreach (var reference in basket.References)
                {
                    var price = basket.PriceEntries.SingleOrDefault(entry => entry.Reference.Id == reference.Id);
                    long? divisor = null;
                    if (price?.PriceDivisor is decimal value)
                    {
                        QuotationMoney.ValidatePriceDivisor(value);
                        divisor = checked((long)(value * 1_000_000m));
                    }
                    entries.Add(reference.Id, (ScaleConversionFactor(price?.ConversionFactor ?? 1m), divisor));
                }
            }
            return entries;
        }

        async Task CreateCopyAsync(Guid id, QuotationBasket basket)
        {
            if (!Enum.IsDefined(basket.AggregationMethod)) throw new ArgumentOutOfRangeException(nameof(basket));
            var baseName = string.IsNullOrWhiteSpace(basket.Name) ? "Manual — Cesta automática" : $"Manual — {basket.Name}";
            var name = baseName;
            for (var suffix = 2; ; suffix++)
            {
                await using var exists = Command("SELECT 1 FROM quotation_manual_baskets WHERE line_id = $line AND name = $name;",
                    ("$line", lineId.ToString("N")), ("$name", name));
                if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) break;
                name = $"{baseName} ({suffix})";
            }
            await using var create = Command("""
                INSERT INTO quotation_manual_baskets(id, line_id, name, display_order, created_at, updated_at, calculation_method)
                VALUES($id, $line, $name, COALESCE((SELECT MAX(display_order) + 1 FROM quotation_manual_baskets WHERE line_id = $line), 0), $now, $now, $method);
                """, ("$id", id.ToString("N")), ("$line", lineId.ToString("N")),
                ("$name", name),
                ("$now", FormatDateTime(DateTimeOffset.UtcNow)), ("$method", (int)basket.AggregationMethod));
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task InsertEntryAsync(Guid basketId, string referenceId, (long Factor, long? Divisor) entry)
        {
            await using var insert = Command("""
                INSERT INTO quotation_manual_basket_references(basket_id, line_id, reference_id, display_order, conversion_factor_millionths)
                VALUES($basket, $line, $reference, COALESCE((SELECT MAX(display_order) + 1 FROM quotation_manual_basket_references WHERE basket_id = $basket), 0), $factor);
                """, ("$basket", basketId.ToString("N")), ("$line", lineId.ToString("N")),
                ("$reference", referenceId), ("$factor", entry.Factor));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (entry.Divisor is long divisor)
            {
                await using var saveDivisor = Command("INSERT INTO quotation_manual_price_divisors(basket_id, reference_id, price_divisor_millionths) VALUES($basket, $reference, $divisor);",
                    ("$basket", basketId.ToString("N")), ("$reference", referenceId), ("$divisor", divisor));
                await saveDivisor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
