using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteQuotationRepository
{
    public async Task<IReadOnlyList<QuotationDuplicatePrice>> FindDuplicatePricesAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Keep the membership scan and the targeted calculation on one read snapshot.
        using var transaction = connection.BeginTransaction(deferred: true);
        var scan = await ReadDuplicatePricesAsync(connection, transaction, projectId, null, cancellationToken).ConfigureAwait(false);
        return scan.Duplicates;
    }

    private sealed record PriceMember(QuotationPricePreview Price, string TaxId, string Control, string Portal,
        QuotationReferenceSource Source);

    private sealed record PendingBasket(Guid LineId, string LineName, string Key, string Name,
        bool Automatic, int Places, QuotationAggregationMethod Method, List<PriceMember> Members,
        List<(string Supplier, string Purchase, QuotationPricePreview Kept, QuotationPricePreview Removed)> Pairs);

    private static async Task<List<PendingBasket>> FindAffectedBasketsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid projectId, IReadOnlySet<Guid>? lineIds, CancellationToken cancellationToken)
    {
        var hasDivisors = await HasPriceDivisorsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH lines AS (
                SELECT l.id, l.display_order, COALESCE(NULLIF(l.display_name, ''), l.description) AS name,
                       l.selected_basket_key, l.selection_confirmed, CASE p.is_medication WHEN 1 THEN 4 ELSE 2 END AS places
                  FROM quotation_lines l JOIN quotation_projects p ON p.id = l.project_id
                 WHERE p.id = $project {(lineIds is null ? "" : "AND l.id IN (SELECT value FROM json_each($lines))")}
            ), members AS (
                SELECT l.id AS line_id, l.display_order AS line_order, l.name AS line_name,
                       'manual:' || b.id AS basket_key, b.name AS basket_name, b.display_order AS basket_order,
                       m.display_order AS member_order, m.reference_id, 0 AS automatic, l.places,
                       b.calculation_method AS method, m.conversion_factor_millionths AS factor,
                       {(hasDivisors ? "d.price_divisor_millionths" : "NULL")} AS divisor
                  FROM lines l JOIN quotation_manual_baskets b ON b.line_id = l.id
                  JOIN quotation_manual_basket_references m ON m.basket_id = b.id AND m.line_id = l.id
                  {(hasDivisors ? "LEFT JOIN quotation_manual_price_divisors d ON d.basket_id = b.id AND d.reference_id = m.reference_id" : "")}
                UNION ALL
                SELECT l.id, l.display_order, l.name, l.selected_basket_key, 'Automática confirmada', 0, 0, r.id,
                       1, l.places, 0, 1000000, NULL
                  FROM lines l LEFT JOIN quotation_references r ON r.line_id = l.id
                   AND instr('||' || l.selected_basket_key || '||', '||' || r.id || '||') > 0
                 WHERE l.selection_confirmed = 1 AND l.selected_basket_key <> '' AND l.selected_basket_key NOT LIKE 'manual:%'
            )
            SELECT m.line_id, m.line_name, m.basket_key, m.basket_name, m.automatic,
                   r.id, r.supplier_name, r.unit_price_scaled, r.result_date, r.supplier_tax_id, r.contract_id, r.portal_url,
                   r.source_kind, m.factor, m.divisor, m.places, m.method
              FROM members m LEFT JOIN quotation_references r ON r.line_id = m.line_id AND r.id = m.reference_id
             ORDER BY m.line_order, m.line_id, m.automatic, m.basket_order, m.basket_key, m.member_order, m.reference_id;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("N"));
        if (lineIds is not null)
            command.Parameters.AddWithValue("$lines", JsonSerializer.Serialize(lineIds.Select(id => id.ToString("N"))));
        var affected = new List<PendingBasket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        while (hasRow)
        {
            var line = reader.GetString(0);
            var key = reader.GetString(2);
            var members = new List<PriceMember>();
            var basket = new PendingBasket(Guid.ParseExact(line, "N"), reader.GetString(1), key, reader.GetString(3),
                reader.GetBoolean(4), reader.GetInt32(15), (QuotationAggregationMethod)reader.GetInt32(16), members, []);
            do
            {
                if (!reader.IsDBNull(5))
                    members.Add(new(new(reader.GetString(5), reader.GetString(6), reader.GetInt64(7) / DecimalScale.Scale,
                        ParseDate(reader, 8), members.Count + 1, reader.GetInt64(13) / 1_000_000m,
                        reader.IsDBNull(14) ? null : reader.GetInt64(14) / 1_000_000m), reader.GetString(9), reader.GetString(10),
                        reader.GetString(11), (QuotationReferenceSource)reader.GetInt32(12)));
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            } while (hasRow && reader.GetString(0) == line && reader.GetString(2) == key);

            if (basket.Automatic)
            {
                if (members.Count < 2 || members.Any(m => m.Source != QuotationReferenceSource.PncpIncisoII ||
                        m.Price.ReferenceId.Contains("||", StringComparison.Ordinal)) ||
                    string.Join("||", members.Select(m => m.Price.ReferenceId).Order(StringComparer.Ordinal)) != key)
                    throw new InvalidOperationException($"Não foi possível identificar toda a cesta confirmada de “{basket.LineName}”. Revise a seleção desse item.");
                members = members.OrderBy(m => QuotationMoney.EffectivePrice(m.Price.UnitPrice, 1m, null, basket.Places))
                    .ThenBy(m => m.Price.ReferenceId, StringComparer.Ordinal)
                    .Select((m, index) => m with { Price = m.Price with { Position = index + 1 } }).ToList();
                basket = basket with { Members = members, Method = QuotationAggregationMethod.Mean };
            }

            var first = new Dictionary<(string Supplier, string Purchase), QuotationPricePreview>();
            foreach (var member in members)
            {
                if (member.Source != QuotationReferenceSource.PncpIncisoII ||
                    member.TaxId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '/' or '-' or ' ')) ||
                    !QuotationAnalyzer.IsValidCnpj(member.TaxId)) continue;
                var purchase = PurchaseIdentity(member);
                if (purchase is null) continue;
                var supplier = NormalizeTaxId(member.TaxId);
                if (!first.TryAdd((supplier, purchase), member.Price))
                    basket.Pairs.Add((supplier, purchase, first[(supplier, purchase)], member.Price));
            }
            if (basket.Pairs.Count > 0) affected.Add(basket);
        }
        return affected;
    }

    private static async Task<(List<QuotationDuplicatePrice> Duplicates, Dictionary<Guid, string[]> AutomaticMembers)>
        ReadDuplicatePricesAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId,
            IReadOnlySet<Guid>? lineIds, CancellationToken cancellationToken)
    {
        var affected = await FindAffectedBasketsAsync(connection, transaction, projectId, lineIds, cancellationToken).ConfigureAwait(false);
        var duplicates = new List<QuotationDuplicatePrice>();
        var automaticMembers = new Dictionary<Guid, string[]>();
        if (affected.Count == 0) return (duplicates, automaticMembers);

        // Score only members of affected baskets. The same manual calculation is used after reopening or import.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LineSelectSql + " WHERE l.project_id = $project AND l.id IN (SELECT value FROM json_each($lines));";
        command.Parameters.AddWithValue("$project", projectId.ToString("N"));
        command.Parameters.AddWithValue("$lines", JsonSerializer.Serialize(affected.Select(b => b.LineId.ToString("N")).Distinct()));
        var lines = new Dictionary<Guid, QuotationLine>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var line = ReadLine(reader);
                lines.Add(line.Id, line);
            }

        var memberIds = affected.SelectMany(b => b.Members.Select(m => (Line: b.LineId, Id: m.Price.ReferenceId))).Distinct();
        command.CommandText = ReferenceSelectSql + """
             WHERE (line_id, id) IN (SELECT value ->> 0, value ->> 1 FROM json_each($members));
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(memberIds.Select(m => new[] { m.Line.ToString("N"), m.Id })));
        var analyzer = new QuotationAnalyzer();
        var references = new Dictionary<(Guid, string), QuotationReference>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var reference = ReadReference(reader);
                references.Add((reference.LineId, reference.Id), analyzer.ScoreReference(lines[reference.LineId], reference));
            }

        foreach (var basket in affected)
        {
            var removed = basket.Pairs.Select(pair => pair.Removed.ReferenceId).ToHashSet(StringComparer.Ordinal);
            var kept = basket.Members.Select(m => m.Price).Where(p => !removed.Contains(p.ReferenceId)).ToArray();
            var line = lines[basket.LineId];
            var manual = new QuotationManualBasket
            {
                Id = basket.Automatic ? Guid.Empty : Guid.ParseExact(basket.Key["manual:".Length..], "N"),
                LineId = line.Id, Name = basket.Name, AggregationMethod = basket.Method,
                ReferenceIds = kept.Select(p => p.ReferenceId).ToArray(),
                ConversionFactors = kept.ToDictionary(p => p.ReferenceId, p => p.ConversionFactor),
                PriceDivisors = kept.Where(p => p.PriceDivisor.HasValue).ToDictionary(p => p.ReferenceId, p => p.PriceDivisor!.Value)
            };
            var result = QuotationAnalyzer.CalculateManualBasket(manual,
                manual.ReferenceIds.Select(id => references[(line.Id, id)]).ToArray(), basket.Places);
            var minimum = basket.Automatic ? line.RequestedBasketSize : result.RequestedSize;
            var block = result.AdoptedPrice <= 0m ? "Nenhum preço válido. A cesta atual será preservada para revisão."
                : minimum != result.RequestedSize ? $"A cópia manual exige {result.RequestedSize} preços. A automática será preservada com o mínimo original de {minimum}."
                : null;
            var validation = minimum != result.RequestedSize && kept.Length < minimum
                ? $"Cesta incompleta para o mínimo original de {minimum} preços." : result.ValidationMessage;
            var impact = new QuotationCleanupImpact(line.SelectedBasketKey, line.SelectionConfirmed, basket.Members.Count,
                kept.Length, minimum, basket.Places, basket.Method, result.AdoptedPrice, validation,
                !result.IsValid, block, JsonSerializer.Serialize(basket.Members));
            foreach (var pair in basket.Pairs)
                duplicates.Add(new(line.Id, basket.LineName, basket.Key, basket.Name, pair.Supplier, pair.Purchase,
                    pair.Kept, pair.Removed, impact));
            if (basket.Automatic) automaticMembers.Add(line.Id, manual.ReferenceIds.ToArray());
        }
        return (duplicates, automaticMembers);
    }

    private static string? PurchaseIdentity(PriceMember member)
    {
        var control = CanonicalPurchase(member.Control.Trim());
        var separator = member.Price.ReferenceId.IndexOf('|');
        var embedded = separator > 0 ? CanonicalPurchase(member.Price.ReferenceId[..separator]) : null;
        string? portal = null;
        if (Uri.TryCreate(member.Portal, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "pncp.gov.br" or "www.pncp.gov.br")
        {
            if (PncpContractKey.TryParse("", uri.AbsoluteUri, out var key) && key is not null)
                portal = CanonicalPurchase($"{key.Cnpj}-1-{key.PurchaseSequence}/{key.PurchaseYear}");
            else if (uri.AbsolutePath.Trim('/').Split('/') is ["api", "pncp", "v1", "orgaos", var org, "compras", var year, var seq, ..])
                portal = CanonicalPurchase($"{org}-1-{seq}/{year}");
        }

        // A recognized conflicting official address or embedded ID vetoes removal. Only the
        // control number or an official HTTPS address can establish the purchase on its own.
        var identity = control ?? (uri?.Scheme == Uri.UriSchemeHttps ? portal : null);
        return (control is not null && control != identity) || (portal is not null && portal != identity) ||
            (embedded is not null && embedded != identity) ? null : identity;
    }

    private static string? CanonicalPurchase(string value)
    {
        if (!PurchaseControlNumber().IsMatch(value) || !PncpContractKey.TryParse(value, null, out var key) ||
            key is not { PurchaseSequence: > 0 } || !QuotationAnalyzer.IsValidCnpj(key.Cnpj)) return null;
        return string.Create(CultureInfo.InvariantCulture, $"{key.Cnpj.ToUpperInvariant()}-1-{key.PurchaseSequence:D6}/{key.PurchaseYear:D4}");
    }

    [GeneratedRegex(@"\A[A-Za-z0-9]{12}[0-9]{2}-1-[0-9]{1,9}/[1-9][0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PurchaseControlNumber();

    public async Task<IReadOnlySet<Guid>> RemoveDuplicatePricesAsync(
        Guid projectId, IReadOnlyList<QuotationDuplicatePrice> preview,
        IReadOnlySet<(Guid LineId, string BasketKey)>? acceptedWarnings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var changed = new HashSet<Guid>();
        if (!preview.Any(row => row.CanRemove(acceptedWarnings))) return changed;
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadDuplicatePricesAsync(connection, transaction, projectId,
            preview.Select(row => row.LineId).ToHashSet(), cancellationToken).ConfigureAwait(false);
        if (!current.Duplicates.SequenceEqual(preview))
            throw new InvalidOperationException("As cestas mudaram desde a prévia. Confira novamente os preços duplicados.");

        await using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM quotation_manual_basket_references WHERE basket_id = $basket AND line_id = $line AND reference_id = $price;";
        remove.Parameters.Add("$basket", SqliteType.Text);
        remove.Parameters.Add("$line", SqliteType.Text);
        remove.Parameters.Add("$price", SqliteType.Text);
        var now = FormatDateTime(DateTimeOffset.UtcNow);
        foreach (var basket in current.Duplicates.Where(row => row.CanRemove(acceptedWarnings)).GroupBy(row => (row.LineId, row.BasketKey)))
        {
            var lineId = basket.Key.LineId.ToString("N");
            changed.Add(basket.Key.LineId);
            if (basket.First().IsAutomatic)
            {
                await SaveCleanedSelectionAsync(connection, transaction, lineId, basket.Key.BasketKey,
                    current.AutomaticMembers[basket.Key.LineId], now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var basketId = basket.Key.BasketKey["manual:".Length..];
            remove.Parameters["$basket"].Value = basketId;
            remove.Parameters["$line"].Value = lineId;
            foreach (var row in basket)
            {
                remove.Parameters["$price"].Value = row.Removed.ReferenceId;
                if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("O preço já não pertence à cesta.");
            }

            await using var touch = connection.CreateCommand();
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE quotation_manual_baskets SET updated_at = $now WHERE id = $basket;";
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$basket", basketId);
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var project = connection.CreateCommand();
        project.Transaction = transaction;
        project.CommandText = "UPDATE quotation_projects SET updated_at = $now WHERE id = $project;";
        project.Parameters.AddWithValue("$now", now);
        project.Parameters.AddWithValue("$project", projectId.ToString("N"));
        await project.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static async Task SaveCleanedSelectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string lineId, string previousKey, IEnumerable<string> keptIds, string now, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var key = $"manual:{id}";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Use the same persisted basket and membership format as an ordinary manual copy.
        command.CommandText = """
            INSERT INTO quotation_manual_baskets(id, line_id, name, display_order, created_at, updated_at, calculation_method)
            SELECT $basket, $line, 'Cesta ' || (next_order + 1) || ' — automática revisada', next_order, $now, $now, $method
              FROM (SELECT COALESCE(MAX(display_order) + 1, 0) AS next_order FROM quotation_manual_baskets WHERE line_id = $line);
            INSERT INTO quotation_manual_basket_references(basket_id, line_id, reference_id, display_order, conversion_factor_millionths)
            SELECT $basket, $line, value, key, $factor FROM json_each($kept);
            """;
        command.Parameters.AddWithValue("$basket", id);
        command.Parameters.AddWithValue("$line", lineId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$method", (int)QuotationAggregationMethod.Mean);
        command.Parameters.AddWithValue("$factor", ScaleConversionFactor(1m));
        command.Parameters.AddWithValue("$kept", JsonSerializer.Serialize(keptIds));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = """
            UPDATE quotation_lines SET selected_basket_key = $key
             WHERE id = $line AND selection_confirmed = 1 AND selected_basket_key = $previous;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$line", lineId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$previous", previousKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("A cesta confirmada mudou desde a prévia.");
    }
}
