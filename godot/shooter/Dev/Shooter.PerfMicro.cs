using Drycastle.Shooter.Dev;

namespace Drycastle.Shooter;

public partial class Shooter
{
    void BuildPerfMicro(Config cfg)
    {
        var m = new PerfMicro { World = this, Map = _map, Water = _water, Vehicles = _data.Vehicles, OutDir = cfg.Str("perf-out", "out/perf/micro") };
        AddChild(m);
    }
}
