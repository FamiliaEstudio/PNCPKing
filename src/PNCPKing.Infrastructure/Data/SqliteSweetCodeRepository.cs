using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Data;

public sealed class SqliteSweetCodeRepository : ISweetCodeRepository
{
    private readonly ISqliteConnectionFactory _connections;

    public SqliteSweetCodeRepository(string databasePath)
        : this(new SqliteConnectionFactory(databasePath))
    {
    }

    public SqliteSweetCodeRepository(ISqliteConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<SweetCodeLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var enabledCommand = connection.CreateCommand();
        enabledCommand.CommandText = "SELECT enabled FROM sweet_code_settings WHERE id = 1;";
        var enabled = Convert.ToInt64(
            await enabledCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;

        await using var codesCommand = connection.CreateCommand();
        codesCommand.CommandText = "SELECT expression FROM sweet_codes ORDER BY position;";
        var expressions = new List<string>();
        await using var reader = await codesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            expressions.Add(reader.GetString(0));
        }

        return new SweetCodeLibrary(enabled, expressions);
    }

    public async Task SaveAsync(
        bool enabled,
        IReadOnlyList<string> expressions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = (SqliteTransaction)transaction;
            settings.CommandText = "UPDATE sweet_code_settings SET enabled = $enabled WHERE id = 1;";
            settings.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM sweet_codes;";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO sweet_codes(position, expression) VALUES($position, $expression);";
            insert.Parameters.Add("$position", SqliteType.Integer);
            insert.Parameters.Add("$expression", SqliteType.Text);
            for (var index = 0; index < expressions.Count; index++)
            {
                insert.Parameters["$position"].Value = index;
                insert.Parameters["$expression"].Value = expressions[index];
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sweet_code_settings SET enabled = $enabled WHERE id = 1;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        return await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
