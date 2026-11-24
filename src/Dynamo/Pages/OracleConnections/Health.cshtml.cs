using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.OracleConnections;

/// <summary>One row of the health page: a connection and, once tried, what the probe found (null = not yet).</summary>
public sealed record HealthRowModel(OracleConnectionInfo Connection, OracleConnector.ProbeResult? Probe);

/// <summary>
/// One page showing every connection's state, login time and Oracle version. The page comes up at once
/// with a "testing" row per connection; each row then asks for its own probe (htmx, on load), so a slow or
/// unreachable database delays only its own row.
/// </summary>
public class HealthModel(OracleConnectionRepository repo, OracleConnector connector) : PageModel
{
    public List<OracleConnectionInfo> Connections { get; private set; } = [];

    public void OnGet()
    {
        Connections = repo.GetAll();
    }

    /// <summary>Probes one connection and answers its row.</summary>
    public async Task<IActionResult> OnPostProbeAsync(long id, CancellationToken ct)
    {
        var connection = repo.Get(id);
        if (connection is null)
            return NotFound();
        var probe = await connector.ProbeAsync(connection, ct);
        return Partial("_HealthRow", new HealthRowModel(connection, probe));
    }
}
