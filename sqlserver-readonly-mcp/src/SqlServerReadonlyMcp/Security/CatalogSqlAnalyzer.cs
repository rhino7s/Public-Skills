using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerReadonlyMcp.Security;

public sealed record CatalogObject(string Database, string Schema, string Name, bool IsFunction);
public sealed record CatalogAnalysis(IReadOnlyList<CatalogObject> Objects, SqlSafetyResult Safety);

/// <summary>只收集调用 SQL 的直接入口；不展开模块依赖，不检查业务参数值。</summary>
public sealed class CatalogSqlAnalyzer
{
    internal const int MaximumObjects = 128;
    internal const int MaximumSqlCharacters = 262_144;
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
        "PERCENT_RANK", "CUME_DIST", "PERCENTILE_CONT", "PERCENTILE_DISC", "STRING_AGG",
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET",
        "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATEPART", "DAY", "MONTH", "YEAR",
        "EOMONTH", "DATEFROMPARTS", "DATETIMEFROMPARTS", "DATETIME2FROMPARTS", "ISDATE",
        "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "SIGN", "EXP", "LOG", "LOG10",
        "ISNULL", "ISNUMERIC", "LEN", "DATALENGTH", "LTRIM", "RTRIM", "TRIM", "LOWER", "UPPER",
        "LEFT", "RIGHT", "SUBSTRING", "REPLACE", "REPLICATE", "REVERSE", "STUFF", "SPACE",
        "CHARINDEX", "PATINDEX", "CONCAT", "CONCAT_WS", "CHAR", "NCHAR", "ASCII", "UNICODE",
        "JSON_VALUE", "JSON_QUERY", "ISJSON", "NEWID"
    };
    // ScriptDom 的部分函数有独立节点；不能因为不属于 FunctionCall 而绕过白名单。
    private static readonly HashSet<string> SpecialCalls = new(StringComparer.Ordinal)
    {
        "FunctionCall", "ParameterlessCall", "CastCall", "TryCastCall", "ConvertCall", "TryConvertCall",
        "ParseCall", "TryParseCall", "IIfCall", "AtTimeZoneCall", "LeftFunctionCall", "RightFunctionCall",
        "IdentityFunctionCall"
    };
    private static readonly HashSet<string> Statements = new(StringComparer.Ordinal)
    {
        "SelectStatement", "DeclareVariableStatement", "DeclareTableVariableStatement", "SetVariableStatement",
        "InsertStatement", "UpdateStatement", "DeleteStatement", "MergeStatement", "CreateTableStatement",
        "DropTableStatement", "TruncateTableStatement"
    };
    private static readonly HashSet<string> Tables = new(StringComparer.Ordinal)
    {
        "NamedTableReference", "SchemaObjectFunctionTableReference", "VariableTableReference",
        "QueryDerivedTable", "InlineDerivedTable", "QualifiedJoin", "UnqualifiedJoin", "JoinParenthesisTableReference",
        "PivotedTableReference", "UnpivotedTableReference"
    };
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Children = new();

    public CatalogAnalysis Analyze(string sql, bool procedure = false, CancellationToken token = default)
    {
        if (sql.Length > MaximumSqlCharacters) return Rejected("SQL 超过长度限制，请缩小批次。");
        token.ThrowIfCancellationRequested();
        var root = new TSql180Parser(true).Parse(new StringReader(sql), out var errors);
        if (errors.Count != 0) return Rejected("SQL 无法解析，请检查语法。");
        var objects = new HashSet<CatalogObject>(); // 精确去重；最终名称等价性由 SQL Server 判定。
        if (procedure)
        {
            var execs = Walk(root).OfType<ExecutableProcedureReference>().ToArray();
            if (execs.Length != 1 || execs[0].ProcedureReference.ProcedureReference?.Name is not { } name)
                return Rejected("只允许单条静态过程调用。");
            return Add(name.Identifiers, false, objects) is { } error ? Rejected(error) : new(objects.ToArray(), SqlSafetyResult.Allowed());
        }

        if (root is not TSqlScript script || script.Batches.Count != 1 || script.TrailingGoCount != 0)
            return Rejected("目录受限查询不支持 GO 批次分隔符。");
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in script.Batches[0].Statements)
        {
            token.ThrowIfCancellationRequested();
            var nodes = Walk(statement).ToArray();
            var ctes = statement is StatementWithCtesAndXmlNamespaces cteStatement
                ? cteStatement.WithCtesAndXmlNamespaces?.CommonTableExpressions.Select(c => c.ExpressionName.Value).ToHashSet(StringComparer.Ordinal) ?? []
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                if (node.GetType().Name.EndsWith("Call", StringComparison.Ordinal) && !SpecialCalls.Contains(node.GetType().Name))
                    return Rejected("目录受限查询不支持此特殊函数语法。");
                if (node is TSqlStatement && !Statements.Contains(node.GetType().Name)) return Rejected("目录受限查询包含未支持的语句。");
                if (node is TableReference && !Tables.Contains(node.GetType().Name)) return Rejected("目录受限查询包含未支持的数据源。");
                if (node is UserDataTypeReference or NextValueForExpression || node.GetType().Name is "GlobalVariableExpression" or "UserDefinedTypeCallTarget")
                    return Rejected("目录受限查询不支持系统变量、序列或用户自定义类型调用。");
                if (node is ParameterlessCall pc && pc.ParameterlessCallType != ParameterlessCallType.CurrentTimestamp)
                    return Rejected("目录受限查询不支持此系统信息函数。");
                if (node.GetType().Name is "PartitionFunctionCall" or "ForeignKeyConstraintDefinition"
                    || node is XmlDataTypeReference { XmlSchemaCollection: not null })
                    return Rejected("目录受限查询不支持分区函数、外键引用或 XML schema 集合。");
                if (node is OptimizerHint or TableHint) return Rejected("目录受限查询暂不支持查询提示或表提示。");
                if (node is SelectStatement { Into: not null } select)
                {
                    if (!Local(select.Into)) return Rejected("仅允许 INTO 本地临时表。");
                    temporary.Add(select.Into.BaseIdentifier.Value);
                }
                if (node is CreateTableStatement create)
                {
                    if (!Local(create.SchemaObjectName)) return Rejected("仅允许创建本地临时表。");
                    temporary.Add(create.SchemaObjectName.BaseIdentifier.Value);
                }
                if (node is NamedTableReference table)
                {
                    var name = table.SchemaObject;
                    if (name.Identifiers.Count == 1 && (ctes.Contains(name.BaseIdentifier.Value) || temporary.Contains(name.BaseIdentifier.Value))) continue;
                    if (Add(name.Identifiers, false, objects) is { } error) return Rejected(error);
                }
                if (node is SchemaObjectFunctionTableReference tvf && Add(tvf.SchemaObject.Identifiers, true, objects) is { } tvfError)
                    return Rejected(tvfError);
                if (node is FunctionCall function)
                {
                    if (function.CallTarget is null)
                    {
                        if (!Functions.Contains(function.FunctionName.Value)) return Rejected("目录受限查询包含未支持的内置函数。");
                    }
                    else if (function.CallTarget is MultiPartIdentifierCallTarget target)
                    {
                        var parts = target.MultiPartIdentifier.Identifiers.Concat([function.FunctionName]).ToArray();
                        if (Add(parts, true, objects) is { } error) return Rejected(error);
                    }
                    else return Rejected("目录受限查询不支持此函数调用方式。");
                }
                if (objects.Count > MaximumObjects) return Rejected($"单次查询最多引用 {MaximumObjects} 个直接对象，请缩小批次。");
            }
            // tempdb 跨库名称也不豁免；临时对象只能在本次连接中使用。
            if (statement is DropTableStatement drop)
                foreach (var name in drop.Objects) temporary.Remove(name.BaseIdentifier.Value);
        }
        return new(objects.ToArray(), SqlSafetyResult.Allowed());
    }

    private static bool Local(SchemaObjectName name) => name.Identifiers.Count == 1
        && name.BaseIdentifier.Value.StartsWith('#') && !name.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal);

    private static string? Add(IEnumerable<Identifier> identifiers, bool function, HashSet<CatalogObject> objects)
    {
        var p = identifiers.Select(x => x.Value).ToArray();
        if (p.Length != 3 || p.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128))
            return "持久化对象必须使用 database.schema.object 完整三段名。";
        if (p[1].Equals("sys", StringComparison.OrdinalIgnoreCase) || p[1].Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
            || p[2].StartsWith('#')) return "目录受限查询不开放系统目录或跨库临时对象。";
        objects.Add(new(p[0], p[1], p[2], function));
        return null;
    }

    // 遍历完整 AST，不依赖特定 FROM 形状；仅枚举 ScriptDom 子节点，避免 token/父属性。
    private static IEnumerable<TSqlFragment> Walk(TSqlFragment root)
    {
        var stack = new Stack<TSqlFragment>();
        stack.Push(root);
        while (stack.TryPop(out var node))
        {
            yield return node;
            foreach (var property in Children.GetOrAdd(node.GetType(), type => type.GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p => typeof(TSqlFragment).IsAssignableFrom(p.PropertyType)
                    || p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(t => typeof(TSqlFragment).IsAssignableFrom(t)))
                .ToArray()))
            {
                if (property.GetValue(node) is TSqlFragment child) stack.Push(child);
                else if (property.GetValue(node) is IEnumerable values)
                    foreach (var value in values.OfType<TSqlFragment>().Reverse()) stack.Push(value);
            }
        }
    }

    private static CatalogAnalysis Rejected(string message) => new([], SqlSafetyResult.Rejected("catalog_sql_not_supported", message));
}
