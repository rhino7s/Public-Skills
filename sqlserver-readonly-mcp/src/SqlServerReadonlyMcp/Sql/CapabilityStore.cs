using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed record CapabilityRow(int Id, int Ord, string Description, bool Oversized = false);

public interface ICapabilityStore
{
    Task<bool> CheckAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<CapabilityRow> ReadAsync(long offset, int take, int maximumCharacters, CancellationToken cancellationToken);
}

public sealed class CapabilityStore(McpSettings settings, SqlConnectionFactory factory, QueryConcurrencyGate gate) : ICapabilityStore
{
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
        command.CommandText = $"SELECT id, ord, desp FROM {function.Sql}() ORDER BY ord, id OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = offset;
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (reader.FieldCount != 3 || reader.GetFieldType(0) != typeof(int) || reader.GetFieldType(1) != typeof(int)
            || reader.GetFieldType(2) != typeof(string))
            throw new InvalidDataException("Invalid capability column types.");
        var completed = false;
        try
        {
            var buffer = new char[maximumCharacters + 1];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var ord = reader.GetInt32(1);
                if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("Capability description cannot be null.");
                using var text = reader.GetTextReader(2);
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await text.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                yield return new(id, ord, new string(buffer, 0, Math.Min(count, maximumCharacters)), count > maximumCharacters);
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
