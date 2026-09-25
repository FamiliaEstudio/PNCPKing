using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed partial class SqliteQuotationRepository
{
    public async Task<IReadOnlyList<QuotationGroup>> GetGroupsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.id, g.name, l.id FROM quotation_groups g
            LEFT JOIN quotation_lines l ON l.group_id = g.id
            WHERE g.project_id = $id ORDER BY g.id, l.id;
            """;
        command.Parameters.AddWithValue("$id", projectId.ToString("N"));
        var names = new Dictionary<Guid, string>();
        var members = new Dictionary<Guid, List<Guid>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.ParseExact(reader.GetString(0), "N");
            names[id] = reader.GetString(1);
            if (!members.TryGetValue(id, out var ids)) members[id] = ids = [];
            if (!reader.IsDBNull(2)) ids.Add(Guid.ParseExact(reader.GetString(2), "N"));
        }
        return names.Select(pair => new QuotationGroup(pair.Key, projectId, pair.Value, members[pair.Key])).ToArray();
    }

    public async Task SaveGroupsAsync(Guid projectId, IReadOnlyList<QuotationGroup> groups, CancellationToken cancellationToken = default)
    {
        var normalized = groups.Where(group => group.LineIds.Count > 0).ToArray();
        var lineIds = normalized.SelectMany(group => group.LineIds).ToArray();
        if (normalized.Any(group => group.Id == Guid.Empty || group.ProjectId != projectId || string.IsNullOrWhiteSpace(group.Name)) ||
            normalized.Select(group => group.Id).Distinct().Count() != normalized.Length || lineIds.Distinct().Count() != lineIds.Length)
            throw new ArgumentException("Cada item deve pertencer a apenas um grupo desta cotação.");
        await using var writer = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        // The immediate SQLite transaction also excludes writers using another connection factory.
        var validIds = (await GetLinesAsync(projectId, cancellationToken).ConfigureAwait(false)).Select(line => line.Id).ToHashSet();
        if (lineIds.Any(id => !validIds.Contains(id))) throw new ArgumentException("Um dos itens não pertence à cotação.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE quotation_projects SET updated_at = $updated WHERE id = $project;";
        command.Parameters.AddWithValue("$project", projectId.ToString("N"));
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("A cotação não existe mais.");
        command.CommandText = "UPDATE quotation_lines SET group_id = NULL WHERE project_id = $project; DELETE FROM quotation_groups WHERE project_id = $project;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$line", SqliteType.Text);
        foreach (var group in normalized)
        {
            command.Parameters["$id"].Value = group.Id.ToString("N");
            command.Parameters["$name"].Value = group.Name.Trim();
            command.Parameters["$line"].Value = string.Empty;
            command.CommandText = "INSERT INTO quotation_groups(id, project_id, name) VALUES($id, $project, $name);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in group.LineIds)
            {
                command.CommandText = "UPDATE quotation_lines SET group_id = $id WHERE id = $line AND project_id = $project;";
                command.Parameters["$line"].Value = id.ToString("N");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveOrganizationAsync(Guid projectId,
        Func<CancellationToken, Task<QuotationOrganizationSnapshot>> calculate,
        CancellationToken cancellationToken = default)
    {
        await using var writer = await _connections.WorkCoordinator.EnterWriterAsync(SqliteWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        // Calculate under the write lock, before any mutation, so every read sees the same committed inputs.
        var snapshot = await calculate(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE quotation_projects SET organization_json = $json, updated_at = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId.ToString("N"));
        command.Parameters.AddWithValue("$json", snapshot.ToJson());
        command.Parameters.AddWithValue("$updated", FormatDateTime(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("A cotação não existe mais.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
