namespace Drycastle.MapLoader.Objects;

/// <summary>A cable between two placed utility poles (objects.json "cables", or the editor's edits file): the ids of the cable and of its two poles.</summary>
public sealed record CableSpec(string Id, string From, string To);
