using Dynamo.Modules.Queries;

namespace Dynamo.Tests;

public class SqlScannerTests
{
    [Test]
    public void Binds_AreFoundInOrder_WithoutDuplicates()
    {
        var a = SqlScanner.Analyze("SELECT * FROM t WHERE a = :id AND b = :name AND c = :ID");

        Assert.That(a.Error, Is.Null);
        Assert.That(a.Binds, Is.EqualTo(new[] { "id", "name" }));
    }

    [Test]
    public void Binds_InStringsAndComments_AreIgnored()
    {
        var sql = """
            SELECT ':notbind', q'[ :alsonot ]', "a:b" FROM dual -- :comment
            /* :block */ WHERE x = :real
            """;

        var a = SqlScanner.Analyze(sql);

        Assert.That(a.Error, Is.Null);
        Assert.That(a.Binds, Is.EqualTo(new[] { "real" }));
    }

    [Test]
    public void Assignment_AndPositionalBinds_AreNotNamedBinds()
    {
        var a = SqlScanner.Analyze("SELECT 1 FROM dual WHERE x = :1 AND y := 2");

        Assert.That(a.Binds, Is.Empty);
    }

    [Test]
    public void ClosingSemicolon_IsRemoved()
    {
        Assert.That(SqlScanner.Analyze("SELECT 1 FROM dual;").Sql, Is.EqualTo("SELECT 1 FROM dual"));
        Assert.That(SqlScanner.Analyze("SELECT 1 FROM dual; -- done").Error, Is.Null);
    }

    [Test]
    public void SemicolonInsideALiteral_IsFine()
    {
        var a = SqlScanner.Analyze("SELECT 'a;b' FROM dual");

        Assert.That(a.Error, Is.Null);
        Assert.That(a.Sql, Is.EqualTo("SELECT 'a;b' FROM dual"));
    }

    [Test]
    public void TwoStatements_AreRefused()
    {
        Assert.That(SqlScanner.Analyze("SELECT 1 FROM dual; SELECT 2 FROM dual").Error,
            Does.Contain("one statement"));
    }

    [TestCase("DELETE FROM t")]
    [TestCase("update t set a = 1")]
    [TestCase("DROP TABLE t")]
    [TestCase("BEGIN NULL; END")]
    [TestCase("INSERT INTO t VALUES (1)")]
    [TestCase("-- select\nDELETE FROM t")]
    [TestCase("/* SELECT */ truncate table t")]
    public void NonSelect_IsRefused(string sql)
    {
        Assert.That(SqlScanner.Analyze(sql).Error, Does.Contain("Only SELECT"));
    }

    [TestCase("select 1 from dual")]
    [TestCase("  \n SELECT 1 FROM dual")]
    [TestCase("WITH x AS (SELECT 1 a FROM dual) SELECT a FROM x")]
    [TestCase("(SELECT 1 FROM dual)")]
    [TestCase("-- a comment first\nSELECT 1 FROM dual")]
    public void Select_IsAccepted(string sql)
    {
        Assert.That(SqlScanner.Analyze(sql).Error, Is.Null);
    }

    [Test]
    public void Empty_IsRefused()
    {
        Assert.That(SqlScanner.Analyze("  -- nothing\n").Error, Is.Not.Null);
    }

    [Test]
    public void UnterminatedLiteral_DoesNotHang()
    {
        Assert.That(() => SqlScanner.Analyze("SELECT 'abc"), Throws.Nothing);
        Assert.That(() => SqlScanner.Analyze("SELECT /* abc"), Throws.Nothing);
        Assert.That(() => SqlScanner.Analyze("SELECT \"abc"), Throws.Nothing);
        Assert.That(() => SqlScanner.Analyze("SELECT q'[abc"), Throws.Nothing);
    }
}
