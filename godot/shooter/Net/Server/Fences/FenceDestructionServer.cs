using System.Text.Json;
using Godot;
using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.Shooter.Fences;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Fences;
using Drycastle.ShooterCore.Net;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Fences;

/// <summary>
/// The breaking of the thin fences on the server (docs/fence-destruction.md, WP-S). The map's fences.json (manifest <c>files.fences</c>; none, the arena, or
/// fence_destruction.json <c>enabled: false</c>: nothing here does anything) gives every thin section its pieces; the hit points live here only (<see cref="FenceState"/>).
/// What breaks them: a round that flies through one (<see cref="Shot"/>, fed by <see cref="Scenery.SceneryServer"/>), a blast (<see cref="Blast"/>, by <see cref="Combat.WorldBlasts"/>)
/// and a vehicle driven into one (<see cref="Drive"/>, every ground vehicle every tick). The pieces that broke in a tick go out once, a message a section with the push of their fall
/// (<see cref="EventKind.FenceBreak"/>), and the server colliders follow once a tick (<see cref="Flush"/>), so a gap is a gap for movement, shots and blasts. A joiner gets what is
/// broken (<see cref="EventKind.FenceSync"/>); a new round, or <c>/fences restore</c>, makes every fence whole. Walls are house destruction's grids, not here.
/// </summary>
internal sealed class FenceDestructionServer
{
    readonly ServerContext _ctx;
    readonly FenceDestructionConfig _cfg;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly IReadOnlyList<FenceSectionInfo> _sections;
    readonly FenceIndex? _index;
    readonly FenceState? _state;
    readonly FenceColliders? _colliders;
    /// <summary>The pieces that broke this tick by section, with the sum of their push velocities (flat, m/s).</summary>
    readonly Dictionary<int, Pending> _pending = new();
    readonly List<PieceHit> _hits = new();
    readonly List<(ushort Section, byte Piece)> _touched = new();

    struct Pending
    {
        public ulong Pieces;
        public NVec2 Push;
        public int Count;
    }

    public FenceDestructionServer(ServerContext ctx, FenceDestructionConfig cfg, bool arenaMode, string mapDir, Drycastle.MapLoader.MapLoader? map)
    {
        _ctx = ctx; _cfg = cfg; _map = map;
        _sections = arenaMode || !cfg.Enabled || mapDir == "" ? Array.Empty<FenceSectionInfo>() : Load(mapDir);
        if (!_sections.Any(s => s.Thin)) return;
        _index = new FenceIndex(_sections);
        _state = new FenceState(_sections, cfg);
        if (map != null) _colliders = new FenceColliders(map, _sections);
        GD.Print($"server: fence destruction on, {_sections.Count(s => s.Thin)} thin fence sections");
    }

    /// <summary>Whether any thin fence can break on this map.</summary>
    public bool On => _state != null;

    /// <summary>The sections of the map's fences.json; none when the manifest lists no file or it is missing. A broken file is logged and counts as none.</summary>
    static IReadOnlyList<FenceSectionInfo> Load(string mapDir)
    {
        try
        {
            var manifest = Path.Combine(mapDir, "manifest.json");
            if (!File.Exists(manifest)) return Array.Empty<FenceSectionInfo>();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty("fences", out var f) || f.GetString() is not { } name) return Array.Empty<FenceSectionInfo>();
            var path = Path.Combine(mapDir, name);
            if (!File.Exists(path)) { GD.PrintErr($"server: {path} is listed in the manifest but missing; fences are not destructible"); return Array.Empty<FenceSectionInfo>(); }
            return FenceFile.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PrintErr($"server: {e.Message}; fences are not destructible");
            return Array.Empty<FenceSectionInfo>();
        }
    }

    // ---- frames ----

    /// <summary>The map root's transform in the world (identity off the tree or with no map): the fences are in map-root local axes.</summary>
    Transform3D ToLocal => _map == null ? Transform3D.Identity : HouseVoxelCollider.MapToWorld(_map.Root).AffineInverse();

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    // ---- what breaks ----

    /// <summary>
    /// A round that flew <paramref name="length"/> m from <paramref name="from"/> along unit <paramref name="dir"/> (Godot world) to where it stopped: every standing piece its path
    /// crosses takes its kind's bullet_damage. The village's thin fences are not solid (rules props.fences.collision), so a round flies on through them; where they are, it stops on
    /// the near face, and the path is followed a little further to reach the section's middle line.
    /// </summary>
    public void Shot(NVec3 from, NVec3 dir, float length)
    {
        if (_state == null || !(length > 0)) return;
        var xf = ToLocal;
        var d = N(xf.Basis * new Vector3(dir.X, dir.Y, dir.Z));
        var o = N(xf * new Vector3(from.X, from.Y, from.Z));
        _touched.Clear();
        FenceRules.PathPieces(_sections, _index!, _state, o, d, length + FenceRules.ShotSlackM + MaxHalfThickness, _touched);
        foreach (var (sec, piece) in _touched)
            if (_state.Damage(sec, piece, _cfg.Of(_sections[sec].Kind).BulletDamage)) Broke(sec, piece, new NVec2(d.X, d.Z) * _cfg.Debris.PushSpeed);
    }

    /// <summary>Half the thickest thin fence's thickness: how far a stopped round's path is followed past the near face (with the slack).</summary>
    const float MaxHalfThickness = 0.1f;

    /// <summary>A blast at <paramref name="at"/> (Godot world) of <paramref name="splashRadius"/>: every standing piece within radius * <c>blast.radius_share</c> takes its share, falling off with distance.</summary>
    public void Blast(Vector3 at, float splashRadius)
    {
        if (_state == null) return;
        var radius = splashRadius * _cfg.Blast.RadiusShare;
        if (!(radius > 0)) return;
        var c = N(ToLocal * at);
        _hits.Clear();
        FenceRules.Blast(_sections, _index!, _state, c, radius, _cfg.Blast.Damage, _hits);
        foreach (var h in _hits)
        {
            if (!_state.Damage(h.Section, h.Piece, h.Damage)) continue;
            var p = _sections[h.Section].PieceCentre(h.Piece);
            var away = new NVec2(p.X - c.X, p.Z - c.Z);
            var near = 1 - Math.Min(1, away.Length() / radius);
            Broke(h.Section, h.Piece, away.LengthSquared() < 1e-6f ? NVec2.Zero : NVec2.Normalize(away) * _cfg.Debris.PushSpeed * (1 + 2 * near));
        }
    }

    /// <summary>
    /// A ground vehicle this tick (docs/fence-destruction.md F6), called for every one, moving or not. The village's thin fences are not solid, so a vehicle drives into them
    /// without losing speed: it is its own speed that counts, or, where they are solid and it stopped on one, the speed it lost (<see cref="ServerCar.LastImpact"/>, along
    /// <see cref="ServerCar.LastImpactVelocity"/>). Below <c>ram.min_speed</c>, or no standing piece its box touches shifted <c>ram.probe_m</c> along that velocity: null.
    /// The pieces cost <c>momentum_per_hp</c> times their hit points; its momentum above that: they all break and it goes on slowed by what they cost. Less: each takes its
    /// share of the momentum as damage and the vehicle stops. Returns the speed a stop on a solid fence cost when it went through (its crash), else null.
    /// </summary>
    public float? Drive(ServerCar car)
    {
        var rc = _cfg.Ram;
        if (_state == null || !rc.Enabled || car.Flies) return null;
        var impact = car.LastImpact >= rc.MinSpeed;
        var v = impact ? car.LastImpactVelocity : car.BumpBody().Velocity;
        var speed = v.Length();
        if (speed < rc.MinSpeed) return null;
        var xf = ToLocal;
        var cs = car.ContactShape();
        var dir = N(xf.Basis * new Vector3(v.X, v.Y, v.Z)) / speed;
        var box = new FenceRamBox(N(xf * new Vector3(cs.Feet.X, cs.Feet.Y, cs.Feet.Z)), cs.Yaw, cs.HalfWidth, cs.HalfLength, cs.Height);
        _touched.Clear();
        FenceRules.RamPieces(_sections, _index!, _state, box, dir, rc.ProbeM, _touched);
        if (_touched.Count == 0) return null;
        var cost = 0f;
        foreach (var (sec, piece) in _touched) cost += _state.Hp(sec, piece) * rc.MomentumPerHp;
        var mass = car.BumpBody().Mass;
        var lost = impact ? car.LastImpact : speed; // not solid: stopping would take all of its speed
        var outcome = HouseRam.Decide(cost, mass, lost, speed);
        var push = new NVec2(dir.X, dir.Z) * speed;
        if (!outcome.Through)
        {
            var share = mass * lost / Math.Max(cost, 1e-3f); // of every hit point: the momentum over the cost
            foreach (var (sec, piece) in _touched)
                if (_state.Damage(sec, piece, Math.Max(1, (int)(_state.Hp(sec, piece) * share)))) Broke(sec, piece, push);
            if (!impact) car.Rammed(System.Numerics.Vector3.Zero); // a fence that is not solid stops it as a solid one would
            return null;
        }
        foreach (var (sec, piece) in _touched)
            if (_state.Damage(sec, piece, _state.Hp(sec, piece))) Broke(sec, piece, push);
        car.Rammed(v * (outcome.SpeedKept / speed));
        return impact ? outcome.SpeedLost : null;
    }

    /// <summary>Piece <paramref name="piece"/> of <paramref name="section"/> broke with the push velocity <paramref name="push"/> (flat, m/s): batched for the end of the tick.</summary>
    void Broke(int section, int piece, NVec2 push)
    {
        _pending.TryGetValue(section, out var p);
        p.Pieces |= 1UL << piece;
        p.Push += push;
        p.Count++;
        _pending[section] = p;
        _colliders?.Mark(section);
    }

    /// <summary>The tick's breaks go out, a message a section, and the colliders of the touched nodes are rebuilt, each once (called once at the end of the tick).</summary>
    public void Flush()
    {
        if (_state == null) return;
        foreach (var (section, p) in _pending)
        {
            var v = p.Push / p.Count;
            var speed = v.Length();
            var (x, z) = speed < 1e-3f ? (0, 0) : (v.X / speed, v.Y / speed);
            _ctx.Broadcast(EventKind.FenceBreak, FenceCodec.EncodeBreak(new FenceBreakMsg((ushort)section, p.Pieces, (sbyte)Math.Round(x * 127), (sbyte)Math.Round(z * 127), (byte)Math.Min(255, Math.Round(speed * 10)))));
        }
        _pending.Clear();
        _colliders?.Flush(_state);
    }

    // ---- rounds and catch-up ----

    /// <summary>A new round: every fence whole, and everyone told.</summary>
    public void RoundStart() => RestoreAll();

    void RestoreAll()
    {
        if (_state == null) return;
        var damaged = _state.Damaged;
        _pending.Clear();
        _state.Restore();
        _colliders?.RestoreAll();
        if (!damaged) return;
        foreach (var m in FenceCodec.EncodeSync(new FenceSyncMsg(true, Array.Empty<(ushort, ulong)>()))) _ctx.Broadcast(EventKind.FenceSync, m);
    }

    /// <summary>The joiner's catch-up: the broken pieces of every section that has some (nothing when the fences are whole).</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_state is not { Damaged: true }) return;
        var sections = _state.BrokenSections().Select(i => ((ushort)i, _state.Broken(i))).ToList();
        foreach (var m in FenceCodec.EncodeSync(new FenceSyncMsg(false, sections))) _ctx.SendEvent(c, EventKind.FenceSync, m);
    }

    // ---- admin commands ----

    /// <summary>"/fences restore": every fence whole now, for everyone; the result to the admin only.</summary>
    public void ChatFences(ServerClient c, IReadOnlyList<string> args)
    {
        if (!On) { _ctx.ChatTo(c, ChatLineKind.Error, "Fence destruction is off on this map."); return; }
        RestoreAll();
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} restored every fence.");
    }
}
