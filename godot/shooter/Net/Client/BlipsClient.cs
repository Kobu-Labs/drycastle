using Drycastle.Shooter.Dev;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Vector2 = System.Numerics.Vector2;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Gunfire blips on the minimap and tactical map. The server broadcasts every Shot and PelletFan to every client with the shooter and
/// origin, so a shooter out of sight blips too; this client only applies the rules (<see cref="GunfireBlipRules"/>) and the radius from match.json.
/// </summary>
public sealed class BlipsClient
{
    readonly ClientContext _ctx;
    readonly MatchConfig? _config;
    GunfireBlips? _blips;

    /// <param name="config">match.json as the client shipped it (null: no blips, e.g. headless bots).</param>
    public BlipsClient(ClientContext ctx, MatchConfig? config) { _ctx = ctx; _config = config; }

    public void Add(ushort shooter, byte weapon, System.Numerics.Vector3 origin)
    {
        if (_ctx.Hud is not { } hud || _config is not { } cfg || _ctx.Weapons is not { } weapons || _ctx.Me is not { } me) return;
        if (_blips == null)
        {
            _blips = new GunfireBlips(cfg.GunfireBlipRadius, cfg.GunfireBlipSeconds, cfg.GunfireBlipInterval);
            hud.Minimap.SetBlips(_blips);
        }
        var match = _ctx.Match;
        var kind = weapon < weapons.Weapons.Count ? weapons.Weapons[weapon].Kind : WeaponKind.Melee;
        var known = match.Rows.Any(r => r.Id == shooter);
        if (!GunfireBlipRules.Qualifies(shooter, _ctx.MyId, known, match.AreFriends(_ctx.MyId, shooter), weapon, weapons.Weapons.Count, kind)) return;
        var g = me.GlobalPosition;
        _blips.Add(shooter, new Vector2(origin.X, origin.Z), new Vector2(g.X, g.Z), _ctx.Now);
    }
}
