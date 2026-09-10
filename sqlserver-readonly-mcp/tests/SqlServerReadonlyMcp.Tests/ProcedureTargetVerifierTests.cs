using SqlServerReadonlyMcp.Security;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class ProcedureTargetVerifierTests
{
    [Theory]
    [InlineData("EXEC sp_test @x = N'a';", "EXEC [Db].[dbo].[sp_test] @x = N'a';")]
    [InlineData("EXEC dbo.sp_test 1;", "EXEC [Db].[dbo].[sp_test] 1;")]
    [InlineData("EXEC Db..sp_test;", "EXEC [Db].[dbo].[sp_test];")]
    [InlineData("/* before */ EXEC [Db].[dbo].[sp_test] N'dbo.sp_test'; -- after", "/* before */ EXEC [Db].[dbo].[sp_test] N'dbo.sp_test'; -- after")]
    public void QualifiesOnlyTargetAndPreservesArguments(string sql, string expected)
    {
        var result = new SqlSafetyAnalyzer().AnalyzeProcedureCall(sql, "Db", out var target);
        Assert.True(result.IsAllowed, result.Message);
        Assert.NotNull(target);
        Assert.Equal("dbo", target.Schema);
        Assert.Equal("sp_test", target.Name);
        Assert.Equal(expected, target.Qualify(sql, "Db", "dbo", "sp_test"));
    }

    [Fact]
    public void CanonicalIdentifiersAreEscaped()
    {
        const string sql = "EXEC dbo.sp_test @x = 1;";
        new SqlSafetyAnalyzer().AnalyzeProcedureCall(sql, "Db", out var target);
        Assert.Equal("EXEC [D]]b].[s]]].[sp_test] @x = 1;", target!.Qualify(sql, "D]b", "s]", "sp_test"));
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PC")]
    public void AllowsVerifiedExecutableBusinessProcedure(string type)
    {
        var result = ProcedureTargetVerifier.Validate("dbo", "sp_test", type, false, true, false);
        Assert.Null(result.Error);
        Assert.Equal("sp_test", result.Name);
    }

    [Theory]
    [InlineData("dbo", "sp_test", "P", true, true, false, "safety_rejection")]
    [InlineData("dbo", "sp_help", "P", false, true, true, "safety_rejection")]
    [InlineData("dbo", "sp_test", "P", false, false, false, "permission_denied")]
    [InlineData("dbo", "sp_test", "X", false, true, false, "safety_rejection")]
    [InlineData("dbo", "sp_test", "SN", false, true, false, "safety_rejection")]
    [InlineData("sys", "sp_test", "P", false, true, false, "safety_rejection")]
    [InlineData("dbo", "XP_test", "P", false, true, false, "safety_rejection")]
    public void RejectsUnsafeOrUnauthorizedTargets(string schema, string name, string type,
        bool system, bool execute, bool collision, string category)
    {
        var result = ProcedureTargetVerifier.Validate(schema, name, type, system, execute, collision);
        Assert.Equal(category, result.Error?.Category);
        Assert.Null(result.Name);
    }
}
