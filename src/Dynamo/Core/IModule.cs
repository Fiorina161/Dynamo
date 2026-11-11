namespace Dynamo.Core;

/// <summary>
/// A use case of Dynamo (bookmarks, commands, queries...). A module
/// contributes one navigation entry and owns its tables through ordered
/// migrations. Its pages live under Pages/&lt;Name&gt;.
/// </summary>
public interface IModule
{
    /// <summary>Stable key, used to track the module's applied migrations.</summary>
    string Id { get; }

    /// <summary>Label shown in the navigation.</summary>
    string Title { get; }

    /// <summary>Page the navigation entry links to.</summary>
    string Href { get; }

    /// <summary>Position in the navigation (lowest first).</summary>
    int Order { get; }

    /// <summary>
    /// SQL scripts applied in order, each exactly once. Never edit a script
    /// that has shipped: append a new one.
    /// </summary>
    IReadOnlyList<string> Migrations { get; }
}
