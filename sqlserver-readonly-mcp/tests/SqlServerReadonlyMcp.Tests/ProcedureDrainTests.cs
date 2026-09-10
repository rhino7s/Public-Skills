using System.Data.Common;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class ProcedureDrainTests
{
    [Fact]
    public async Task ReadsAllRemainingSetsWithoutAccessingValues()
    {
        using var reader = new DrainReader();
        await SqlQueryService.DrainAsync(reader, CancellationToken.None);
        Assert.Equal(6, reader.RowsRead);
        Assert.Equal(3, reader.SetsRead);
    }

    [Fact]
    public async Task LateErrorIsPropagatedInsteadOfReportingCompletion()
    {
        using var reader = new DrainReader { FailOnNextResult = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlQueryService.DrainAsync(reader, CancellationToken.None));
        Assert.Equal(2, reader.RowsRead);
    }

    [Fact]
    public async Task CancellationStopsDraining()
    {
        using var reader = new DrainReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlQueryService.DrainAsync(reader, cancellation.Token));
        Assert.Equal(0, reader.RowsRead);
    }

    // No value accessor is implemented: draining must not retain or materialize large cells.
    private sealed class DrainReader : DbDataReader
    {
        public bool FailOnNextResult { get; init; }
        public int RowsRead { get; private set; }
        public int SetsRead { get; private set; }
        private int row;
        public override bool Read() { if (row++ >= 2) return false; RowsRead++; return true; }
        public override bool NextResult()
        {
            if (FailOnNextResult) throw new InvalidOperationException("Late SQL failure");
            row = 0;
            return ++SetsRead < 3;
        }
        public override Task<bool> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read()); }
        public override Task<bool> NextResultAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(NextResult()); }
        public override int FieldCount => 1;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int ordinal] => throw new NotSupportedException();
        public override object this[string name] => throw new NotSupportedException();
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override string GetName(int ordinal) => throw new NotSupportedException();
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override string GetString(int ordinal) => throw new NotSupportedException();
        public override object GetValue(int ordinal) => throw new NotSupportedException();
        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override bool IsDBNull(int ordinal) => throw new NotSupportedException();
    }
}
