using Dynamo.Modules.OracleConnections;

namespace Dynamo.Pages;

/// <summary>Model of the shared connection picker options.</summary>
public sealed record ConnectionOptionsModel(
    IReadOnlyList<ConnectionChoice> Choices, string? Selected);
