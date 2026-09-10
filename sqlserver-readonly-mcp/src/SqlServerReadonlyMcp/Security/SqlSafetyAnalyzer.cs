using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerReadonlyMcp.Security;

public sealed class SqlSafetyAnalyzer
{
    public SqlSafetyResult Analyze(string sql)
    {
        var parseResult = Parse(sql);
        if (parseResult.Error is not null)
        {
            return parseResult.Error;
        }

        var visitor = new ForbiddenStatementVisitor();
        parseResult.Fragment!.Accept(visitor);
        if (visitor.ContainsExecute)
        {
            return SqlSafetyResult.Rejected(
                "execute_not_allowed",
                "禁止 EXEC/EXECUTE，包括执行存储过程和动态 SQL。请直接提交只读查询或读取对象定义。");
        }

        foreach (var token in parseResult.Fragment.ScriptTokenStream ?? [])
        {
            if (token.TokenType is not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier))
            {
                continue;
            }

            var identifier = UnquoteIdentifier(token.Text);
            if (identifier.StartsWith("##", StringComparison.Ordinal))
            {
                return SqlSafetyResult.Rejected(
                    "global_temp_table_not_allowed",
                    "禁止使用全局临时表（##）。可以使用仅限当前连接的本地临时表（#）。");
            }
        }

        if (visitor.Rejection is not null)
        {
            return visitor.Rejection;
        }

        return SqlSafetyResult.Allowed();
    }

    public SqlSafetyResult AnalyzeProcedureCall(string sql, string database) => AnalyzeProcedureCall(sql, database, out _);

    internal SqlSafetyResult AnalyzeProcedureCall(string sql, string database, out ProcedureCallTarget? target)
    {
        target = null;
        var parseResult = Parse(sql);
        if (parseResult.Error is not null)
        {
            return parseResult.Error;
        }

        if (parseResult.Fragment is not TSqlScript script ||
            script.Batches.Count != 1 ||
            script.Batches[0].Statements.Count != 1 ||
            script.TrailingGoCount != 0 ||
            script.Batches[0].Statements[0] is not ExecuteStatement execute)
        {
            return SqlSafetyResult.Rejected(
                "single_procedure_call_required",
                "只允许一条直接的存储过程 EXEC/EXECUTE 语句，不得夹带其他语句或 GO。");
        }

        var specification = execute.ExecuteSpecification;
        if (specification.LinkedServer is not null)
        {
            return SqlSafetyResult.Rejected("remote_execute_not_allowed", "禁止 EXECUTE AT 远程执行。");
        }

        if (specification.ExecuteContext is not null)
        {
            return SqlSafetyResult.Rejected("execute_context_not_allowed", "禁止为 EXEC 指定 LOGIN/USER 执行上下文。");
        }

        if (specification.ExecutableEntity is not ExecutableProcedureReference executable ||
            executable.AdHocDataSource is not null)
        {
            return SqlSafetyResult.Rejected(
                "dynamic_execute_not_allowed",
                "禁止动态 SQL 和临时数据源；只允许直接调用静态命名的存储过程。");
        }

        var procedureName = executable.ProcedureReference;
        if (procedureName.ProcedureReference is null || procedureName.ProcedureVariable is not null)
        {
            return SqlSafetyResult.Rejected(
                "variable_procedure_not_allowed",
                "禁止使用变量指定存储过程名；请直接写出对象名。");
        }

        var name = procedureName.ProcedureReference.Name;
        if (name.ServerIdentifier is not null)
        {
            return SqlSafetyResult.Rejected(
                "linked_server_procedure_not_allowed",
                "禁止四段名链接服务器存储过程；最多允许 database.schema.procedure。");
        }

        if (!string.IsNullOrWhiteSpace(name.DatabaseIdentifier?.Value) &&
            !string.IsNullOrWhiteSpace(database) &&
            !string.Equals(
                name.DatabaseIdentifier.Value,
                database.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return SqlSafetyResult.Rejected(
                "procedure_database_mismatch",
                "存储过程三段名中的数据库必须与 database 参数一致。");
        }

        if (string.Equals(name.BaseIdentifier?.Value, "sp_executesql", StringComparison.OrdinalIgnoreCase))
        {
            return SqlSafetyResult.Rejected("sp_executesql_not_allowed", "禁止通过 sp_executesql 执行动态 SQL。");
        }

        // Keep known SQL executors blocked before connecting. Other system names are checked against the catalog.
        if (string.Equals(name.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase) ||
            IsSqlExecutor(name.BaseIdentifier?.Value) ||
            name.BaseIdentifier?.Value.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) == true)
        {
            return SqlSafetyResult.Rejected(
                "system_procedure_not_allowed",
                "禁止调用 sys 架构、xp_ 前缀或系统动态 SQL 执行入口；业务过程必须通过对象和权限核验。");
        }

        target = new ProcedureCallTarget(name.SchemaIdentifier?.Value is { Length: > 0 } schema ? schema : "dbo",
            name.BaseIdentifier!.Value, name.StartOffset, name.FragmentLength);
        return SqlSafetyResult.Allowed();
    }

    private static bool IsSqlExecutor(string? name) => name is not null &&
        (name.StartsWith("sp_cursor", StringComparison.OrdinalIgnoreCase) ||
         new[] { "sp_prepare", "sp_prepexec", "sp_prepexecrpc", "sp_execute", "sp_unprepare", "sp_MSforeachtable", "sp_MSforeachdb" }
             .Contains(name, StringComparer.OrdinalIgnoreCase));

    private static ParseResult Parse(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new(null, SqlSafetyResult.Rejected("empty_sql", "SQL 不可为空。"));
        }

        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            var details = string.Join("；", errors.Take(5).Select(error =>
                $"第 {error.Line} 行、第 {error.Column} 列：{error.Message}"));
            return new(null, SqlSafetyResult.Rejected("parse_error", details));
        }

        return new(fragment, null);
    }

    private static string UnquoteIdentifier(string text)
    {
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            return text[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return text;
    }

    private sealed class ForbiddenStatementVisitor : TSqlFragmentVisitor
    {
        public bool ContainsExecute { get; private set; }

        public SqlSafetyResult? Rejection { get; private set; }

        public override void Visit(TSqlStatement node)
        {
            if (Rejection is not null)
            {
                return;
            }

            if (node is DataModificationStatement)
            {
                // A #name target can bind to a FROM alias or CTE instead of a temp
                // table. Reserve that prefix in this statement's bindings, including
                // derived tables and nested sources; do not guess database collation.
                var bindings = new TemporaryNameBindingVisitor();
                node.Accept(bindings);
                if (bindings.HasTemporaryNameBinding)
                {
                    Rejection = SqlSafetyResult.Rejected(
                        "temporary_name_binding_not_allowed",
                        "写入语句中的表别名和 CTE 名称不得以 # 开头；请改用普通名称，并直接指定实际的 #本地临时表或 @表变量作为写入目标。");
                    return;
                }
            }

            switch (node)
            {
                case InsertStatement insert:
                    RejectPersistentDataModification(insert.InsertSpecification, "INSERT");
                    return;
                case UpdateStatement update:
                    RejectPersistentDataModification(update.UpdateSpecification, "UPDATE");
                    return;
                case DeleteStatement delete:
                    RejectPersistentDataModification(delete.DeleteSpecification, "DELETE");
                    return;
                case MergeStatement merge:
                    RejectPersistentDataModification(merge.MergeSpecification, "MERGE");
                    return;
                case SelectStatement select when select.Into is not null && !IsLocalTemporaryName(select.Into):
                    RejectPersistentWrite("SELECT INTO");
                    return;
                case CreateTableStatement createTable:
                    RejectUnlessLocalTemporary(createTable.SchemaObjectName, "CREATE TABLE");
                    return;
                case AlterTableStatement alterTable:
                    RejectUnlessLocalTemporary(alterTable.SchemaObjectName, "ALTER TABLE");
                    return;
                case DropTableStatement dropTable:
                    if (dropTable.Objects.Any(name => !IsLocalTemporaryName(name)))
                    {
                        RejectPersistentWrite("DROP TABLE");
                    }

                    return;
                case TruncateTableStatement truncateTable:
                    RejectUnlessLocalTemporary(truncateTable.TableName, "TRUNCATE TABLE");
                    return;
                case UseStatement:
                    Rejection = SqlSafetyResult.Rejected(
                        "database_switch_not_allowed",
                        "禁止使用 USE 切换数据库；请通过 database 参数指定初始数据库，并用三段名进行明确的跨库只读查询。");
                    return;
            }

            var statementName = node.GetType().Name;
            if (HasPersistentDefinitionPrefix(statementName) ||
                node is BulkInsertStatement or DbccStatement or CheckpointStatement or
                    ReconfigureStatement or ShutdownStatement or SetIdentityInsertStatement or
                    SetUserStatement or ApplicationRoleStatement)
            {
                RejectPersistentWrite(statementName);
            }
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            ContainsExecute = true;
        }

        public override void ExplicitVisit(ExecuteInsertSource node)
        {
            ContainsExecute = true;
        }

        public override void ExplicitVisit(ExecuteAsStatement node)
        {
            ContainsExecute = true;
        }

        public override void ExplicitVisit(DataModificationTableReference node)
        {
            Rejection ??= SqlSafetyResult.Rejected(
                "nested_dml_not_allowed",
                "禁止将嵌套 INSERT、UPDATE、DELETE 或 MERGE 作为数据源。请改用只读查询或分步处理本地临时资料。");
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.SchemaObject.ServerIdentifier is not null)
            {
                RejectExternalDataSource("链接服务器四段名");
            }
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            if (node.SchemaObject.ServerIdentifier is not null)
            {
                RejectExternalDataSource("链接服务器四段名函数");
            }
        }

        public override void ExplicitVisit(OpenQueryTableReference node)
        {
            RejectExternalDataSource("OPENQUERY");
        }

        public override void ExplicitVisit(AdHocTableReference node)
        {
            RejectExternalDataSource("OPENDATASOURCE");
        }

        public override void ExplicitVisit(OpenRowsetTableReference node)
        {
            RejectExternalDataSource("OPENROWSET");
        }

        public override void ExplicitVisit(BulkOpenRowset node)
        {
            RejectExternalDataSource("OPENROWSET(BULK...)");
        }

        public override void ExplicitVisit(OpenRowsetCosmos node)
        {
            RejectExternalDataSource("OPENROWSET");
        }

        public override void ExplicitVisit(InternalOpenRowset node)
        {
            RejectExternalDataSource("OPENROWSET");
        }

        public override void ExplicitVisit(NextValueForExpression node)
        {
            Rejection ??= SqlSafetyResult.Rejected(
                "sequence_mutation_not_allowed",
                "禁止使用 NEXT VALUE FOR；取得序列值会修改持久化序列状态。");
        }

        private void RejectUnlessLocalTemporary(SchemaObjectName name, string operation)
        {
            if (!IsLocalTemporaryName(name))
            {
                RejectPersistentWrite(operation);
            }
        }

        private void RejectPersistentDataModification(
            DataModificationSpecification specification,
            string operation)
        {
            if (!IsEphemeralTarget(specification.Target) ||
                specification.OutputIntoClause?.IntoTable is { } outputTarget &&
                !IsEphemeralTarget(outputTarget))
            {
                RejectPersistentWrite(operation);
            }
        }

        private void RejectPersistentWrite(string operation)
        {
            Rejection = SqlSafetyResult.Rejected(
                "persistent_write_not_allowed",
                $"禁止对持久化对象执行 {operation}。只允许查询，以及对 #本地临时表或 @表变量进行临时处理。");
        }

        private void RejectExternalDataSource(string source)
        {
            Rejection ??= SqlSafetyResult.Rejected(
                "external_data_source_not_allowed",
                $"禁止使用显式远程或 Ad Hoc 数据源（{source}）。只允许当前 SQL Server 实例内的本地或三段名跨库查询。");
        }

        private static bool IsEphemeralTarget(TableReference target) => target switch
        {
            VariableTableReference => true,
            NamedTableReference named => IsLocalTemporaryName(named.SchemaObject),
            _ => false,
        };

        private static bool IsLocalTemporaryName(SchemaObjectName name) =>
            name.BaseIdentifier?.Value.StartsWith("#", StringComparison.Ordinal) == true &&
            !name.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal);

        private static bool HasPersistentDefinitionPrefix(string statementName) =>
            statementName.StartsWith("Create", StringComparison.Ordinal) ||
            statementName.StartsWith("Alter", StringComparison.Ordinal) ||
            statementName.StartsWith("Drop", StringComparison.Ordinal) ||
            statementName.StartsWith("Truncate", StringComparison.Ordinal) ||
            statementName.StartsWith("Grant", StringComparison.Ordinal) ||
            statementName.StartsWith("Deny", StringComparison.Ordinal) ||
            statementName.StartsWith("Revoke", StringComparison.Ordinal) ||
            statementName.StartsWith("Backup", StringComparison.Ordinal) ||
            statementName.StartsWith("Restore", StringComparison.Ordinal) ||
            statementName.StartsWith("Kill", StringComparison.Ordinal) ||
            statementName.StartsWith("Add", StringComparison.Ordinal) ||
            statementName.StartsWith("Enable", StringComparison.Ordinal) ||
            statementName.StartsWith("Disable", StringComparison.Ordinal) ||
            statementName.StartsWith("Insert", StringComparison.Ordinal) ||
            statementName.StartsWith("Update", StringComparison.Ordinal) ||
            statementName.StartsWith("Write", StringComparison.Ordinal) ||
            statementName.StartsWith("Rename", StringComparison.Ordinal) ||
            statementName.StartsWith("Copy", StringComparison.Ordinal) ||
            statementName.StartsWith("Send", StringComparison.Ordinal) ||
            statementName.StartsWith("Receive", StringComparison.Ordinal) ||
            statementName.StartsWith("BeginDialog", StringComparison.Ordinal) ||
            statementName.StartsWith("BeginConversation", StringComparison.Ordinal) ||
            statementName.StartsWith("EndConversation", StringComparison.Ordinal) ||
            statementName.StartsWith("MoveConversation", StringComparison.Ordinal) ||
            statementName.StartsWith("GetConversation", StringComparison.Ordinal);
    }

    private sealed class TemporaryNameBindingVisitor : TSqlFragmentVisitor
    {
        public bool HasTemporaryNameBinding { get; private set; }

        public override void Visit(TableReferenceWithAlias node) => CheckName(node.Alias);

        public override void Visit(CommonTableExpression node) => CheckName(node.ExpressionName);

        private void CheckName(Identifier? name)
        {
            // Also reserve full-width compatibility spellings of the # prefix.
            if (name?.Value.Normalize(System.Text.NormalizationForm.FormKC)
                    .StartsWith("#", StringComparison.Ordinal) == true)
            {
                HasTemporaryNameBinding = true;
            }
        }
    }

    private sealed record ParseResult(TSqlFragment? Fragment, SqlSafetyResult? Error);
}

public sealed record SqlSafetyResult(bool IsAllowed, string? Code, string? Message)
{
    public static SqlSafetyResult Allowed() => new(true, null, null);

    public static SqlSafetyResult Rejected(string code, string message) => new(false, code, message);
}

internal sealed record ProcedureCallTarget(string Schema, string Name, int StartOffset, int Length)
{
    internal string Qualify(string sql, string database, string schema, string name) =>
        sql[..StartOffset] + Quote(database) + "." + Quote(schema) + "." + Quote(name) + sql[(StartOffset + Length)..];
    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
