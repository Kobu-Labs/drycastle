namespace Drycastle.ShooterCore.Jobs;

/// <summary>
/// Player-facing text the server writes that names a key: the server does not know the player's bindings, so it writes the token (<see cref="Interact"/>) and the client puts the
/// bound key's name in (<see cref="Fill"/>), as its own prompts do.
/// </summary>
public static class JobText
{
    /// <summary>The interact key ("Hold {interact} to hand the duffel over").</summary>
    public const string Interact = "{interact}";

    /// <summary>The line with every token replaced by the key's name; the line itself when it has none (no allocation).</summary>
    public static string Fill(string line, string interactKey) =>
        line.Contains(Interact, StringComparison.Ordinal) ? line.Replace(Interact, interactKey, StringComparison.Ordinal) : line;
}
