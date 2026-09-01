using Microsoft.Data.SqlClient;

namespace SqlServerReadonlyMcp.Sql;

internal static class SqlErrorClassifier
{
    internal static ToolError Create(SqlException exception) => new(
        Categorize(exception.Number),
        Limit(exception.Message),
        exception.Number,
        exception.State,
        exception.Class);

    internal static string Categorize(int errorNumber) => errorNumber switch
    {
        -2 => "timeout",
        229 or 230 or 262 or 300 or 916 => "permission_denied",
        4060 or 18456 => "authentication_or_database",
        2 or 20 or 40 or 53 or 64 or 233 or 10053 or 10054 or 10060 => "connection_error",
        _ => "sql_error",
    };

    private static string Limit(string value) => value.Length <= 2_048 ? value : value[..2_048];
}
