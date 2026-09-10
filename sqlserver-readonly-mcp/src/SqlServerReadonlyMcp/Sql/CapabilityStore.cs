using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed record CapabilityRow(int Id, int Ord, string Description, bool Oversized = false, string? ObjectName = null);

public interface ICapabilityStore
{
    Task<bool> IsProcedureGrantedAsync(string database, string schema, string name, CancellationToken cancellationToken);
    Task<bool> CheckAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<CapabilityRow> ReadAsync(long offset, int take, int maximumCharacters, CancellationToken cancellationToken);
}

public sealed class CapabilityStore(McpSettings settings, SqlConnectionFactory factory, QueryConcurrencyGate gate) : ICapabilityStore
{
    internal const string ProcedureGrantPredicate = """
        PARSENAME(iname, 4) IS NULL
        AND DB_ID(PARSENAME(iname, 3)) = DB_ID()
        AND PARSENAME(iname, 3) IS NOT NULL
        AND PARSENAME(iname, 2) COLLATE CATALOG_DEFAULT = @schema COLLATE CATALOG_DEFAULT
        AND PARSENAME(iname, 1) COLLATE CATALOG_DEFAULT = @name COLLATE CATALOG_DEFAULT
        """;

    public async Task<bool> IsProcedureGrantedAsync(string database, string schema, string name, CancellationToken cancellationToken)
    {
        var function = CapabilityFunctionName.Parse(settings.Capabilities.ListFunction);
        using var lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        // Object identifiers use the target catalog collation, which can differ from its data collation.
        await using var connection = await factory.OpenAsync(database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.Query.TimeoutSeconds;
        command.CommandText = $"SELECT TOP (1) iname FROM {function.Sql}() WHERE {ProcedureGrantPredicate};";
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetFieldType(0) != typeof(string)) throw new InvalidDataException("Invalid capability iname type.");
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && !reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0));
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        var function = CapabilityFunctionName.Parse(settings.Capabilities.CheckFunction);
        using var lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(function.Database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.Query.TimeoutSeconds;
        command.CommandText = $"SELECT {function.Sql}();";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return ReadCheckValue(value);
    }

    internal static bool ReadCheckValue(object? value) => value is bool allowed ? allowed :
        throw new InvalidDataException("Capability check must return a non-null SQL bit.");

    public async IAsyncEnumerable<CapabilityRow> ReadAsync(long offset, int take, int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var function = CapabilityFunctionName.Parse(settings.Capabilities.ListFunction);
        using var lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(function.Database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.Query.TimeoutSeconds;
        command.CommandText = $"SELECT id, ord, iname, desp FROM {function.Sql}() ORDER BY ord, id OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = offset;
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (reader.FieldCount != 4 || reader.GetFieldType(0) != typeof(int) || reader.GetFieldType(1) != typeof(int)
            || reader.GetFieldType(2) != typeof(string) || reader.GetFieldType(3) != typeof(string))
            throw new InvalidDataException("Invalid capability column types.");
        var completed = false;
        try
        {
            var buffer = new char[maximumCharacters + 1];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var ord = reader.GetInt32(1);
                var objectName = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("Capability description cannot be null.");
                using var text = reader.GetTextReader(3);
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await text.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                yield return new(id, ord, new string(buffer, 0, Math.Min(count, maximumCharacters)), count > maximumCharacters, objectName);
            }
            completed = true;
        }
        finally
        {
            // Avoid draining an arbitrarily large remainder when the caller stops at a page/size limit.
            if (!completed) command.Cancel();
        }
    }
}
