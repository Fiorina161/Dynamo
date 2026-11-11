using System.Text;
using Dynamo.Modules.Queries;

namespace Dynamo.Tests;

public class QueryCsvTests
{
    private static QueryResult Result(params string?[][] rows) =>
        new([new ResultColumn("ID", true), new ResultColumn("TEXT", false)], rows, false, 1);

    private static string Text(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes, Encoding.UTF8.GetPreamble().Length, bytes.Length - 3);

    [Test]
    public void StartsWithABom_SoExcelReadsUtf8()
    {
        var bytes = QueryCsv.Write(Result());

        Assert.That(bytes.Take(3), Is.EqualTo(Encoding.UTF8.GetPreamble()));
    }

    [Test]
    public void WritesHeader_AndRows_WithCrLf()
    {
        var csv = Text(QueryCsv.Write(Result(["1", "a"], ["2", "b"])));

        Assert.That(csv, Is.EqualTo("ID,TEXT\r\n1,a\r\n2,b\r\n"));
    }

    [Test]
    public void Quotes_CommasQuotesAndLineBreaks()
    {
        var csv = Text(QueryCsv.Write(Result(["1", "a,b"], ["2", "say \"hi\""], ["3", "x\ny"])));

        Assert.That(csv, Is.EqualTo("ID,TEXT\r\n1,\"a,b\"\r\n2,\"say \"\"hi\"\"\"\r\n3,\"x\ny\"\r\n"));
    }

    [Test]
    public void SqlNull_IsAnEmptyField()
    {
        var csv = Text(QueryCsv.Write(Result(["1", null])));

        Assert.That(csv, Is.EqualTo("ID,TEXT\r\n1,\r\n"));
    }

    [Test]
    public void FileName_ReplacesInvalidCharacters()
    {
        var name = QueryCsv.FileName("a/b:c");

        Assert.That(name, Does.StartWith("a_b_c "));
        Assert.That(name, Does.EndWith(".csv"));
        Assert.That(QueryCsv.FileName("  "), Does.StartWith("query "));
    }
}
