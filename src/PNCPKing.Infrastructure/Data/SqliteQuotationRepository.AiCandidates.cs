using System.Globalization;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteQuotationRepository
{
    // Keep only a small write batch in memory. A corrupt/cancelled item rolls back
    // completely, while already committed items can be resumed without duplicates.
    public async Task<QuotationAiCandidateImportResult> ImportAiCandidatesAsync(
        QuotationLine expectedLine, bool isMedication,
        IAsyncEnumerable<QuotationReference> candidates,
        IProgress<QuotationAiProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator
            .EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadAiGuideLineAsync(connection, transaction, expectedLine, cancellationToken).ConfigureAwait(false);
        if (current is null || current.SampleVersion != expectedLine.SampleVersion ||
            QuotationAiPackage.SpecificationHash(current) != QuotationAiPackage.SpecificationHash(expectedLine))
            throw new InvalidDataException("O item mudou desde a prévia. Gere uma nova prévia do guia.");
        await using (var project = connection.CreateCommand())
        {
            project.Transaction = transaction;
            project.CommandText = "SELECT is_medication FROM quotation_projects WHERE id = $id;";
            project.Parameters.AddWithValue("$id", expectedLine.ProjectId.ToString("N"));
            var value = await project.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || (Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0) != isMedication)
                throw new InvalidDataException("A precisão da cotação mudou desde a coleta.");
        }

        var pending = new Dictionary<string, QuotationReference>(StringComparer.Ordinal);
        var added = 0;
        long examined = 0;
        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = ReferenceSelectSql + " WHERE line_id = $line AND id = $id;";
        existing.Parameters.AddWithValue("$line", expectedLine.Id.ToString("N"));
        existing.Parameters.Add("$id", SqliteType.Text);
        await foreach (var candidate in candidates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.LineId != expectedLine.Id || string.IsNullOrWhiteSpace(candidate.Id))
                throw new InvalidDataException("Um candidato não pertence ao item do pacote ou não possui identificador.");
            QuotationReference? previous = pending.GetValueOrDefault(candidate.Id);
            if (previous is null)
            {
                existing.Parameters["$id"].Value = candidate.Id;
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) previous = ReadReference(reader);
            }
            if (previous is not null)
            {
                if (!SameAiReference(previous, candidate))
                    throw new InvalidDataException("Uma referência existente diverge do pacote. Os dados locais foram preservados.");
            }
            else pending.Add(candidate.Id, candidate);
            examined++;
            if (pending.Count == 100)
            {
                await InsertReferencesAsync(connection, transaction, expectedLine.Id, pending.Values.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                added += pending.Count;
                pending.Clear();
            }
            if (examined % 100 == 0)
                progress?.Report(new("candidates", 0, 1, examined,
                    $"Importando candidatos de {expectedLine.EffectiveDisplayName}: {examined:N0} conferidos."));
        }
        await InsertReferencesAsync(connection, transaction, expectedLine.Id, pending.Values.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        added += pending.Count;
        if (added > 0)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE quotation_lines SET sample_version = sample_version + 1, sampled_at = $now,
                    selection_confirmed = CASE WHEN selected_basket_key IS NOT NULL
                        AND selected_basket_key NOT LIKE 'manual:%' THEN 0 ELSE selection_confirmed END
                WHERE id = $line;
                UPDATE quotation_projects SET updated_at = $now WHERE id = $project;
                """;
            update.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$line", expectedLine.Id.ToString("N"));
            update.Parameters.AddWithValue("$project", expectedLine.ProjectId.ToString("N"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            current = (await ReadAiGuideLineAsync(connection, transaction, current, cancellationToken).ConfigureAwait(false))!;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new("candidates", 1, 1, examined,
            $"Candidatos de {expectedLine.EffectiveDisplayName}: {added:N0} acrescentados; {examined:N0} conferidos."));
        return new(current, added);
    }
}
