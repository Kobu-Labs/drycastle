namespace Drycastle.ShooterCore.Jobs;

/// <summary>
/// Player-facing text the server writes that names a key: the server does not know the player's bindings, so it writes the token (<see cref="Interact"/>) and the client puts the
/// bound key's name in (<see cref="Fill"/>), as its own prompts do.
/// </summary>
public static class JobText
{
    /// <summary>The interact key ("Hold {interact} to hand the duffel over").</summary>
    public const string Interact = "{interact}";

    /// <summary>The Duffel throw key, which also takes a Duffel out of an open cargo area ("take the duffel out with {duffel_throw}").</summary>
    public const string Throw = "{duffel_throw}";

    /// <summary>The line with every token replaced by the key's name; the line itself when it has none (no allocation). Without <paramref name="throwKey"/> the throw token stays.</summary>
    public static string Fill(string line, string interactKey, string? throwKey = null)
    {
        if (line.Contains(Interact, StringComparison.Ordinal)) line = line.Replace(Interact, interactKey, StringComparison.Ordinal);
        if (throwKey != null && line.Contains(Throw, StringComparison.Ordinal)) line = line.Replace(Throw, throwKey, StringComparison.Ordinal);
        return line;
    }
}
