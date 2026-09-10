using System.Text.RegularExpressions;

namespace SqlServerReadonlyMcp.Configuration;

internal sealed record CapabilityFunctionName(string Database, string Sql)
{
    private const string Identifier = @"(?:\[(?:[^\]\r\n]|\]\])+\]|[\p{L}_][\p{L}\p{N}_@$#]*)";
    private static readonly Regex Pattern = new(
        @"\A\s*(?<db>" + Identifier + @")\s*\.\s*(?<schema>" + Identifier +
        @")\s*\.\s*(?<name>" + Identifier + @")\s*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static CapabilityFunctionName Parse(string value)
    {
        if (value.Length > 800) throw new ArgumentException("函数名过长。");
        var match = Pattern.Match(value);
        if (!match.Success) throw new ArgumentException("函数必须使用 database.dbo.function 三段名称。");
        var parts = new[] { "db", "schema", "name" }.Select(key => Unquote(match.Groups[key].Value)).ToArray();
        if (parts.Any(part => part.Length is 0 or > 128 || part.Any(char.IsControl)) || parts[1] != "dbo")
            throw new ArgumentException("函数标识符最长 128 字符，schema 必须为 dbo。");
        return new(parts[0], string.Join('.', parts.Select(Quote)));
    }

    private static string Unquote(string value) => value.StartsWith('[') ? value[1..^1].Replace("]]", "]") : value;
    private static string Quote(string value) => "[" + value.Replace("]", "]]") + "]";
}
