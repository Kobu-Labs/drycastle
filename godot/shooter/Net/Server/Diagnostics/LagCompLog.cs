using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// The lag-compensation log (measurement plan B1, ADR 0013): <c>--perf-lagcomp</c>. Every hitscan shot is resolved three ways, one CSV row per shot in
/// PerfOut/lagcomp.csv: hit_used is what the server did (rewound, or present with --no-lagcomp); hit_now resolves against the present positions and
/// hit_rewound against the poses the shooter's screen showed. Both comparison columns leave out the walls, as the B1 shadow did.
/// </summary>
internal sealed class LagCompLog
{
    readonly string _dir;
    readonly bool _enabled;
    readonly ServerContext _ctx;
    readonly PoseRewind _rewind;
    readonly ShotWorld _shot;
    readonly TargetBook _targets;
    StreamWriter? _log;

    public LagCompLog(string dir, bool enabled, ServerContext ctx, PoseRewind rewind, ShotWorld shot, TargetBook targets)
    {
        _dir = dir; _enabled = enabled && dir.Length > 0; _ctx = ctx; _rewind = rewind; _shot = shot; _targets = targets;
    }

    public void Shot(ServerClient c, Vector3 eye, Vector3 dir, float range, ushort? hitUsed)
    {
        if (!_enabled) return;
        if (_log == null)
        {
            _log = new StreamWriter(Path.Combine(_dir, "lagcomp.csv")) { AutoFlush = true };
            _log.WriteLine("tick,shooter,rtt_ms,back_ticks,hit_used,hit_now,hit_rewound,same,n_targets,speeds_mps,aimed_id,aimed_dist_m,lateral_rewound_m,lateral_now_m,resolve_us");
        }
        var rtt = PoseRewind.RttOf(c);
        var back = _rewind.BackTicksOf(c);
        var resolveUs = _shot.ResolveUs;
        var cur = _targets.Live();
        var rewoundAll = _rewind.TargetsSeenBy(c);
        var rewound = new List<HitTarget>(rewoundAll.Count);
        foreach (var t in rewoundAll) if (t.Id != c.Id) rewound.Add(t);
        var speeds = new List<string>();
        foreach (var t in rewound)
            foreach (var now in cur)
                if (now.Id == t.Id) { speeds.Add(((now.Feet - t.Feet).Length() / (back / (float)Wire.TickRate)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)); break; }
        var hr = HitMath.Raycast(eye.ToNumerics(), dir.ToNumerics(), range, float.PositiveInfinity, rewound, c.Id);
        var hn = HitMath.Raycast(eye.ToNumerics(), dir.ToNumerics(), range, float.PositiveInfinity, cur, c.Id);
        // the target the shooter aimed at: the rewound one nearest to the ray (chest height), and how far the ray passes from it then and from the same player now
        NVec3 ne = eye.ToNumerics(), nd = dir.ToNumerics();
        float Lat(NVec3 feet) { var v = feet + new NVec3(0, 1.0f, 0) - ne; var al = NVec3.Dot(v, nd); return al <= 0 ? float.MaxValue : (v - nd * al).Length(); }
        ushort aimed = 0; var aimedLat = float.MaxValue; var aimedDist = 0f; var latNow = float.MaxValue;
        foreach (var t in rewound)
        {
            var l = Lat(t.Feet);
            if (l >= aimedLat) continue;
            aimed = t.Id; aimedLat = l; aimedDist = (t.Feet - ne).Length();
            latNow = float.MaxValue;
            foreach (var nowT in cur)
                if (nowT.Id == t.Id) { latNow = Lat(nowT.Feet); break; }
        }
        var a = hn?.Id ?? 0; var b = hr?.Id ?? 0; var used = hitUsed ?? 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _log.WriteLine(_ctx.Tick + "," + c.Id + "," + rtt.ToString("F1", inv) + "," + back + "," + used + "," + a + "," + b + "," + (a == b ? 1 : 0) + "," + rewound.Count + "," + string.Join("|", speeds) + "," + aimed + "," + aimedDist.ToString("F1", inv) + "," + aimedLat.ToString("F2", inv) + "," + latNow.ToString("F2", inv) + "," + resolveUs.ToString("F2", inv));
    }
}
