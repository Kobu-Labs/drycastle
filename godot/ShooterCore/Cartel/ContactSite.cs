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

/// <summary>The map's placed Contact (<see cref="CartelMap.Contact"/>): in reach within <paramref name="reachM"/> ground metres; a map without one has no Cartel.</summary>
public sealed class MapContactSite(Func<CartelMap> map, float reachM) : IContactSite
{
    public bool Present => map().HasCartel;
    public bool InReach(Vector3 at) => map().Contact is { } c && c.GroundDistance(at) <= reachM;
}

/// <summary>A stand-in for tests: he is everywhere.</summary>
public sealed class AnywhereContact : IContactSite
{
    public static readonly AnywhereContact Instance = new();
    public bool Present => true;
    public bool InReach(Vector3 at) => true;
}
