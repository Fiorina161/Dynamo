using Dynamo.Core;
using Dynamo.Modules.OracleConnections;

namespace Dynamo.Tests;

public class TnsNamesTests
{
    private const string MultiLine = """
        # a banner comment
        PROD =
          (DESCRIPTION =
            (ADDRESS = (PROTOCOL = TCP)(HOST = 10.0.0.1)(PORT = 1521))
            (CONNECT_DATA =
              (SERVER = DEDICATED)
              (SERVICE_NAME = prod.example.com)
            )
          )
        """;

    [Test]
    public void MultiLineEntry_BecomesOneLineWithoutBlanks()
    {
        var entries = TnsNamesParser.Parse(MultiLine);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Alias, Is.EqualTo("PROD"));
        Assert.That(entries[0].Descriptor, Is.EqualTo(
            "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=10.0.0.1)(PORT=1521))" +
            "(CONNECT_DATA=(SERVER=DEDICATED)(SERVICE_NAME=prod.example.com)))"));
    }

    [Test]
    public void SingleLineEntry_IsReadToo()
    {
        var entries = TnsNamesParser.Parse(
            "virtualbox = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=1.2.3.4)(PORT=1521))(CONNECT_DATA=(SID=emgs)))");

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "virtualbox" }));
        Assert.That(entries[0].Descriptor, Does.EndWith("(SID=emgs)))"));
    }

    [Test]
    public void SeveralEntries_AndCommentsBetweenThem()
    {
        var entries = TnsNamesParser.Parse($"""
            # first
            {MultiLine}

            #####
            # EMGSORACLE21
            #####

            BLA.DEV = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=bla)))
            """);

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "PROD", "BLA.DEV" }));
    }

    [Test]
    public void CommentInsideAnEntry_IsDropped()
    {
        var entries = TnsNamesParser.Parse("""
            X =
              (DESCRIPTION =
                # (ADDRESS = (PROTOCOL = TCP)(HOST = old)(PORT = 1521))
                (ADDRESS = (PROTOCOL = TCP)(HOST = new)(PORT = 1521))
                (CONNECT_DATA = (SERVICE_NAME = s))
              )
            """);

        Assert.That(entries[0].Descriptor, Does.Contain("HOST=new").And.Not.Contain("old"));
    }

    [Test]
    public void ParenthesisInAComment_DoesNotUnbalanceTheEntry()
    {
        var entries = TnsNamesParser.Parse("""
            A = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = h)(PORT = 1521)) # note :-)
                (CONNECT_DATA = (SERVICE_NAME = s)))
            B = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = h2)(PORT = 1521))(CONNECT_DATA = (SERVICE_NAME = s2)))
            """);

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public void FailoverAddressList_IsKeptWhole()
    {
        var entries = TnsNamesParser.Parse("""
            F =
              (DESCRIPTION =
                (ADDRESS_LIST =
                  (FAILOVER = ON)
                  (ADDRESS = (PROTOCOL = TCP)(HOST = a)(PORT = 1521))
                  (ADDRESS = (PROTOCOL = TCP)(HOST = b)(PORT = 1521))
                )
                (CONNECT_DATA = (SERVICE_NAME = svc))
              )
            """);

        Assert.That(entries[0].Descriptor, Does.Contain("(HOST=a)").And.Contain("(HOST=b)"));
    }

    [Test]
    public void SeveralAliasesOnOneDescriptor_EachGetAnEntry()
    {
        var entries = TnsNamesParser.Parse(
            "A, B.WORLD = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)))");

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "A", "B.WORLD" }));
        Assert.That(entries[0].Descriptor, Is.EqualTo(entries[1].Descriptor));
    }

    [Test]
    public void QuotedValue_KeepsItsBlankAndParenthesis()
    {
        var entries = TnsNamesParser.Parse("Q = (DESCRIPTION=(X=\"a ) b\")(Y=1))");

        Assert.That(entries[0].Descriptor, Is.EqualTo("(DESCRIPTION=(X=\"a ) b\")(Y=1))"));
    }

    [Test]
    public void WindowsLineEndingsAndTabs_AreFine()
    {
        var entries = TnsNamesParser.Parse("A =\r\n\t(DESCRIPTION =\r\n\t\t(ADDRESS = (PROTOCOL = TCP)(HOST = h)(PORT = 1521))\r\n\t)\r\nB = (DESCRIPTION=(X=1))\r\n");

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public void LinesThatAreNotEntries_AreSkipped()
    {
        var entries = TnsNamesParser.Parse("""
            IFILE = c:\other\tnsnames.ora
            just some words
            A = (DESCRIPTION=(X=1))
            """);

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public void UnbalancedEntryAtTheEnd_IsDroppedWithoutHanging()
    {
        var entries = TnsNamesParser.Parse("A = (DESCRIPTION=(X=1))\nB = (DESCRIPTION=(X=1");

        Assert.That(entries.Select(e => e.Alias), Is.EqualTo(new[] { "A" }));
    }

    [TestCase("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=10.0.0.1)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=prod.example.com)))",
        "10.0.0.1:1521/prod.example.com")]
    [TestCase("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SID=emgs)))",
        "h:1521/emgs (SID)")]
    [TestCase("(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=a)(PORT=1521))(ADDRESS=(PROTOCOL=TCP)(HOST=b)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=svc)))",
        "a:1521/svc +1")]
    [TestCase("(description=(address=(protocol=tcp)(host=h)(port=1521))(connect_data=(service_name=s)))",
        "h:1521/s")]
    [TestCase("PRODALIAS", "PRODALIAS")]
    [TestCase("host:1521/service", "host:1521/service")]
    public void Summary_IsReadable(string dataSource, string expected)
    {
        Assert.That(DataSourceSummary.Of(dataSource), Is.EqualTo(expected));
    }

    [Test]
    public void Importer_AddsTheAliases_LeavesCredentialsEmpty_AndIsRepeatable()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-tns-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            var db = new Db(DynamoHome.Resolve());
            db.Migrate([new OracleConnectionsModule()]);
            var repo = new OracleConnectionRepository(db);
            var file = Path.Combine(home, "tnsnames.ora");
            File.WriteAllText(file, """
                A = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h1)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=a)))
                B = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h2)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=b)))
                same = (DESCRIPTION=(X=1))
                SAME = (DESCRIPTION=(X=1))
                diff = (DESCRIPTION=(X=1))
                DIFF = (DESCRIPTION=(X=2))
                """);
            var importer = new TnsNamesImporter(repo);

            var first = importer.Import(file);

            Assert.That(first.Added, Is.EqualTo(4));
            Assert.That(first.Identical, Is.EqualTo(new[] { "same" }));
            Assert.That(first.Conflicting, Is.EqualTo(new[] { "diff" }));
            var a = repo.GetByName("a")!;
            Assert.That(a.Name, Is.EqualTo("A"));
            Assert.That(a.Username, Is.Empty);
            Assert.That(a.Password, Is.Empty);
            Assert.That(a.Complete, Is.False);
            Assert.That(repo.GetByName("diff")!.DataSource, Is.EqualTo("(DESCRIPTION=(X=1))"), "first definition wins");

            // What the user filled in must survive a second import.
            a.Username = "scott";
            a.Password = "tiger";
            repo.Save(a);
            var second = importer.Import(file);

            Assert.That(second.Added, Is.EqualTo(0));
            Assert.That(second.AlreadyThere, Is.EqualTo(4));
            Assert.That(repo.GetByName("A")!.Username, Is.EqualTo("scott"));
            Assert.That(repo.GetByName("A")!.Complete, Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task IncompleteConnection_CannotBeOpenedOrTested_AndSaysWhy()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-tns-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            var db = new Db(DynamoHome.Resolve());
            db.Migrate([new OracleConnectionsModule()]);
            var repo = new OracleConnectionRepository(db);
            repo.Save(new OracleConnectionInfo { Name = "NEW", DataSource = "h:1/s" });
            var connector = new OracleConnector(repo);

            var test = await connector.TestAsync(repo.GetByName("NEW")!);
            var open = await Assert.ThrowsAsync<ConnectionNotReadyException>(() => connector.OpenAsync("NEW"));

            Assert.That(test.Ok, Is.False);
            Assert.That(test.Message, Does.Contain("no schema yet"));
            Assert.That(open!.Message, Does.Contain("no schema yet"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }
}
