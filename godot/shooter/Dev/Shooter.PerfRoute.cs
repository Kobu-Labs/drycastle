using System.Globalization;
using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Dev;

namespace Drycastle.Shooter;

public partial class Shooter
{
    void BuildPerfRoute(Config cfg, Player player)
    {
        static int I(string s, int d) => int.TryParse(s, out var v) ? v : d;
        static float F(string s, float d) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        _capFps = false; // no frame cap while measuring: the 20 fps cap of an unfocused window would be all the route records
        AddChild(new PerfRoute
        {
            Player = player, Map = _map, OutDir = cfg.Str("perf-out", "out/perf/route"), Views = I(cfg.Str("perf-views"), 96), Msaa = I(cfg.Str("perf-msaa"), -1),
            Dwell = F(cfg.Str("perf-dwell"), 2f), NoShadow = cfg.Flag("perf-noshadow"), ShadowDist = F(cfg.Str("perf-shadow-dist"), -1f), BlastRate = F(cfg.Str("perf-blasts"), 0f), Hide = cfg.Str("perf-hide"), NoGlow = cfg.Flag("perf-noglow"), NoFog = cfg.Flag("perf-nofog"), Scale = F(cfg.Str("perf-scale"), -1f), Vsync = cfg.Flag("perf-vsync"), Tag = cfg.Str("perf-tag"), Settle = F(cfg.Str("perf-settle"), 0.6f),
            Anchors = cfg.Str("perf-anchors").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Split(',')).Where(a => a.Length == 3).Select(a => new Vector3(F(a[0], 0f), F(a[1], 0f), F(a[2], 0f))).ToList(),
        });
    }
}
