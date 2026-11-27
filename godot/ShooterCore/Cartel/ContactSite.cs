using System.Numerics;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// Where the Contact stands (docs/cartel.md "Contact"): a start, join or quit request counts only in reach of him. The map's placed Contact implements this (a later package);
/// a map without one has no Cartel.
/// </summary>
public interface IContactSite
{
    /// <summary>The map has a Contact (false: no Cartel, with no error).</summary>
    bool Present { get; }
    /// <summary>A player standing at <paramref name="at"/> can talk to him.</summary>
    bool InReach(Vector3 at);
}

/// <summary>Today's stand-in until the map places the Contact: he is everywhere.</summary>
public sealed class AnywhereContact : IContactSite
{
    public static readonly AnywhereContact Instance = new();
    public bool Present => true;
    public bool InReach(Vector3 at) => true;
}
