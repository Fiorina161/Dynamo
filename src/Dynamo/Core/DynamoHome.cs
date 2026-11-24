namespace Dynamo.Core;

/// <summary>
/// Locates (and creates) the directory that holds everything Dynamo keeps
/// on disk: the Sqlite database and, later, logs and other local data.
/// </summary>
public sealed class DynamoHome
{
    public const string EnvironmentVariable = "DYNAMO_HOME";
    public const string DefaultPath = @"C:\dynamo";

    public string Path { get; }

    public string DatabaseFile => System.IO.Path.Combine(Path, "dynamo.db");

    private DynamoHome(string path)
    {
        Path = path;
    }

    /// <summary>
    /// DYNAMO_HOME when set, otherwise C:\dynamo. The directory is created
    /// if it does not exist.
    /// </summary>
    public static DynamoHome Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var path = string.IsNullOrWhiteSpace(configured)
            ? DefaultPath
            : System.IO.Path.GetFullPath(configured.Trim());
        Directory.CreateDirectory(path);
        return new DynamoHome(path);
    }
}
