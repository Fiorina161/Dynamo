using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>The page frame: on a small screen the menu folds into a hamburger button.</summary>
public class LayoutTests
{
    [Test]
    public async Task TheMenuTitle_ShowsTheInstalledVersion()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-layout-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            var expected = Dynamo.Modules.Settings.AppVersion.Format(Dynamo.Modules.Settings.AppVersion.Current);
            foreach (var path in new[] { "/Bookmarks", "/Settings" })
                Assert.That(await client.GetStringAsync(path),
                    Does.Contain($"Dynamo <span class=\"version\" title=\"Installed version\">{expected}</span>"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task EveryPage_HasTheHamburgerButton_AndTheMenuInOneFoldableBlock()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-layout-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            foreach (var path in new[] { "/Bookmarks", "/Oracle", "/Queries", "/Workplaces", "/Settings" })
            {
                var html = await client.GetStringAsync(path);
                Assert.That(html, Does.Contain("id=\"menu-toggle\"").And.Contain("aria-expanded=\"false\""), path);
                // The links, the theme button and the log-out form are inside what folds.
                var menu = html[html.IndexOf("id=\"side-menu\"", StringComparison.Ordinal)..html.IndexOf("</nav>", StringComparison.Ordinal)];
                Assert.That(menu, Does.Contain("href=\"/Settings\"").And.Contain("id=\"theme-toggle\""), path);
                Assert.That(html.IndexOf("id=\"menu-toggle\"", StringComparison.Ordinal),
                    Is.LessThan(html.IndexOf("id=\"side-menu\"", StringComparison.Ordinal)), "the button stays in the top bar");
            }

            // Each menu entry has its own little icon, drawn inline (no icon site: Dynamo works offline).
            var menuHtml = await client.GetStringAsync("/Bookmarks");
            var icons = System.Text.RegularExpressions.Regex.Matches(menuHtml, "<svg class=\"ico\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">");
            Assert.That(icons, Has.Count.EqualTo(5), "Bookmarks, Connections, Queries, Workspaces, Settings");
            Assert.That(menuHtml, Does.Not.Contain("cdn.").And.Not.Contain("fontawesome"));
            foreach (var module in factory.Services.GetServices<Dynamo.Core.IModule>())
                Assert.That(module.Icon, Does.Match("^<(path|rect|ellipse|circle)"), module.Title);

            // The rules that make it work: hidden on a wide screen, shown below 760px.
            var css = await client.GetStringAsync("/css/site.css");
            Assert.That(css, Does.Contain(".menu-toggle { display: none; }").And.Contain("@media (max-width: 760px)"));
            var script = await client.GetStringAsync("/js/site.js");
            Assert.That(script, Does.Contain("menu-toggle").And.Contain("setMenu"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }
}
