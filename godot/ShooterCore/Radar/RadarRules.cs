namespace Drycastle.ShooterCore;

/// <summary>A vehicle with somebody aboard, as the radar sees it: where it is and the ids of the players seated in it.</summary>
public readonly record struct RadarVehicle(ushort Id, float X, float Z, IReadOnlyList<ushort> Occupants);

/// <summary>
/// Who a radar shows its operator (GLOSSARY "Radar unit"; ADR 0037), on the UAV's filter (<see cref="UavRules"/>: the living players and the target dummies, minus the owner, minus anyone whose perk makes them
/// invisible to it: Ghost). On foot, an enemy is a <see cref="RadarKind.Player"/> contact; a player in a vehicle is not, the vehicle is a <see cref="RadarKind.Vehicle"/> contact, hidden when any enemy aboard has
/// Ghost. An empty vehicle shows nothing. In team deathmatch the operator's teammates are not swept: they come back as friendlies, live and whatever their perks, a vehicle with a teammate aboard as a friendly
/// vehicle. Free-for-all has no friendlies. Pure.
/// </summary>
public static class RadarRules
{
    /// <summary>
    /// Splits the living <paramref name="alive"/> (the UAV's sample: players and targets) and the <paramref name="vehicles"/> with riders into what the sweep may stamp (<paramref name="enemies"/>) and what
    /// is shown live (<paramref name="friendlies"/>), both in id order, for the operator <paramref name="owner"/>.
    /// </summary>
    public static void Split(IReadOnlyList<UavContact> alive, IReadOnlyList<RadarVehicle> vehicles, ushort owner, List<RadarBlip> enemies, List<RadarBlip> friendlies)
    {
        enemies.Clear(); friendlies.Clear();
        var ownerTeam = Teams.None;
        foreach (var c in alive) if (c.Id == owner) ownerTeam = c.Team;
        bool Mate(UavContact c) => ownerTeam != Teams.None && c.Team == ownerTeam;
        var byId = new Dictionary<ushort, UavContact>(alive.Count);
        foreach (var c in alive) byId[c.Id] = c;
        var seated = new HashSet<ushort>();
        foreach (var v in vehicles)
        {
            var enemyAboard = false; var hidden = false; var mateAboard = false;
            foreach (var o in v.Occupants)
            {
                seated.Add(o);
                if (o == owner || !byId.TryGetValue(o, out var c)) continue;
                if (Mate(c)) mateAboard = true;
                else { enemyAboard = true; if (c.Visibility <= 0) hidden = true; }
            }
            if (enemyAboard && !hidden) enemies.Add(new RadarBlip(v.Id, RadarKind.Vehicle, v.X, v.Z));
            if (mateAboard) friendlies.Add(new RadarBlip(v.Id, RadarKind.Vehicle, v.X, v.Z));
        }
        foreach (var c in alive)
        {
            if (c.Id == owner || seated.Contains(c.Id)) continue;
            if (Mate(c)) friendlies.Add(new RadarBlip(c.Id, RadarKind.Player, c.X, c.Z));
            else if (c.Visibility > 0) enemies.Add(new RadarBlip(c.Id, RadarKind.Player, c.X, c.Z));
        }
        enemies.Sort((a, b) => a.Id.CompareTo(b.Id));
        friendlies.Sort((a, b) => a.Id.CompareTo(b.Id));
    }
}
