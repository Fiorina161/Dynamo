using System.Text.RegularExpressions;

namespace Dynamo.Modules.OracleConnections;

/// <summary>
/// A short, readable form of a data source for lists: a full descriptor
/// becomes <c>host:port/service</c> (the first address, "+N" when there are
/// more); an alias or an Easy Connect string is shown as it is.
/// </summary>
public static class DataSourceSummary
{
    private static readonly Regex Host = new(@"\(HOST=([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Port = new(@"\(PORT=([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Service = new(@"\(SERVICE_NAME=([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sid = new(@"\(SID=([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Of(string dataSource)
    {
        var text = dataSource.Trim();
        if (!text.StartsWith('('))
            return text;

        var hosts = Host.Matches(text);
        if (hosts.Count == 0)
            return text;

        var port = Port.Match(text);
        var service = Service.Match(text);
        var sid = Sid.Match(text);

        var summary = hosts[0].Groups[1].Value;
        if (port.Success)
            summary += $":{port.Groups[1].Value}";
        if (service.Success)
            summary += $"/{service.Groups[1].Value}";
        else if (sid.Success)
            summary += $"/{sid.Groups[1].Value} (SID)";
        if (hosts.Count > 1)
            summary += $" +{hosts.Count - 1}";
        return summary;
    }
}
