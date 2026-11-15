using Godot;
using Drycastle.ShooterCore;
using Vector2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// Gunfire blips on the minimap and tactical map. The server broadcasts every Shot and PelletFan to every client with the shooter and
/// origin, so a shooter out of sight blips too; this client only applies the rules (<see cref="GunfireBlipRules"/>) and the radius from match.json.
/// </summary>
public partial class NetClient
{
    GunfireBlips? _blips;

    void GunfireBlip(ushort shooter, byte weapon, System.Numerics.Vector3 origin)
    {
        if (_hud == null || BlipConfig is not { } cfg || Weapons == null || _me == null) return;
        if (_blips == null)
        {
            _blips = new GunfireBlips(cfg.GunfireBlipRadius, cfg.GunfireBlipSeconds, cfg.GunfireBlipInterval);
            _hud.Minimap.SetBlips(_blips);
        }
        var kind = weapon < Weapons.Weapons.Count ? Weapons.Weapons[weapon].Kind : WeaponKind.Melee;
        var known = Match.Rows.Any(r => r.Id == shooter);
        if (!GunfireBlipRules.Qualifies(shooter, _myId, known, Match.AreFriends(_myId, shooter), weapon, Weapons.Weapons.Count, kind)) return;
        var g = _me.GlobalPosition;
        _blips.Add(shooter, new Vector2(origin.X, origin.Z), new Vector2(g.X, g.Z), Time.GetTicksMsec() / 1000.0);
    }
}
