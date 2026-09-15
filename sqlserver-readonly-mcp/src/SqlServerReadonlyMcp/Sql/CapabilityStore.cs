using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed record CapabilityRow(int Id, int Ord, string Summary, bool Oversized = false, string? ObjectName = null, bool HasDescription = false);
public sealed record CapabilityDetail(int Id, string? ObjectName, string Summary, bool HasDescription, string Description, bool Oversized = false);

public interface ICapabilityStore
{
    Task<bool> IsProcedureGrantedAsync(string database, string schema, string name, CancellationToken cancellationToken);
    Task<bool> CheckAsync(CancellationToken cancellationToken);
    Task<CapabilityDetail?> ReadDetailsAsync(int id, int maximumCharacters, CancellationToken cancellationToken);
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

    internal const string SummaryProjection = "id, ord, iname, summary, has_desp";

    public async IAsyncEnumerable<CapabilityRow> ReadAsync(long offset, int take, int maximumCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var function = CapabilityFunctionName.Parse(settings.Capabilities.ListFunction);
        using var lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(function.Database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.Query.TimeoutSeconds;
        command.CommandText = $"SELECT {SummaryProjection} FROM {function.Sql}() ORDER BY ord, id OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = offset;
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        ValidateTypes(reader, typeof(int), typeof(int), typeof(string), typeof(string), typeof(bool));
        var completed = false;
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var ord = reader.GetInt32(1);
                var name = reader.IsDBNull(2) ? (Text: (string?)null, Oversized: false) : await ReadTextAsync(reader, 2, 150, cancellationToken).ConfigureAwait(false);
                var summary = await ReadTextAsync(reader, 3, Math.Min(1000, maximumCharacters), cancellationToken).ConfigureAwait(false);
                var hasDescription = reader.GetBoolean(4);
                yield return new(id, ord, summary.Text, name.Oversized || summary.Oversized, name.Text, hasDescription);
            }
            completed = true;
        }
        finally { if (!completed) command.Cancel(); }
    }

    public async Task<CapabilityDetail?> ReadDetailsAsync(int id, int maximumCharacters, CancellationToken cancellationToken)
    {
        var function = CapabilityFunctionName.Parse(settings.Capabilities.ListFunction);
        using var lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(function.Database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.Query.TimeoutSeconds;
        command.CommandText = $"SELECT TOP (2) id, iname, summary, has_desp, desp FROM {function.Sql}() WHERE id = @id;";
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        ValidateTypes(reader, typeof(int), typeof(string), typeof(string), typeof(bool), typeof(string));
        var completed = false;
        try
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { completed = true; return null; }
            var actualId = reader.GetInt32(0);
            var name = reader.IsDBNull(1) ? (Text: (string?)null, Oversized: false) : await ReadTextAsync(reader, 1, 150, cancellationToken).ConfigureAwait(false);
            var summary = await ReadTextAsync(reader, 2, Math.Min(1000, maximumCharacters), cancellationToken).ConfigureAwait(false);
            var hasDescription = reader.GetBoolean(3);
            var description = await ReadTextAsync(reader, 4, maximumCharacters, cancellationToken).ConfigureAwait(false);
            var result = new CapabilityDetail(actualId, name.Text, summary.Text, hasDescription, description.Text,
                name.Oversized || summary.Oversized || description.Oversized);
            if (result.Oversized) return result;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Duplicate capability id.");
            completed = true;
            return result;
        }
        finally { if (!completed) command.Cancel(); }
    }

    private static void ValidateTypes(SqlDataReader reader, params Type[] types)
    {
        if (reader.FieldCount != types.Length || types.Where((type, index) => reader.GetFieldType(index) != type).Any())
            throw new InvalidDataException("Invalid capability column types.");
    }

    private static async Task<(string Text, bool Oversized)> ReadTextAsync(SqlDataReader reader, int ordinal, int limit, CancellationToken token)
    {
        if (await reader.IsDBNullAsync(ordinal, token).ConfigureAwait(false)) throw new InvalidDataException("Capability text cannot be null.");
        using var text = reader.GetTextReader(ordinal);
        var buffer = new char[limit + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await text.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return (new string(buffer, 0, Math.Min(count, limit)), count > limit);
    }
}
