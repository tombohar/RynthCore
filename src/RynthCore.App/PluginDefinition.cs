namespace RynthCore.App;

internal sealed class PluginDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string StatusText { get; init; }
    public bool RuntimeImplemented { get; init; }
}
