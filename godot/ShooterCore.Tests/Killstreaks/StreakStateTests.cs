using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Tests.Killstreaks;

/// <summary>
/// Killstreak state machines: the tracker's slot state (client), the service's per-life hold state and the per-use lifecycle (server). The tracker is checked
/// against the three-HashSet model it replaced, over a long random event stream, so its derived states cannot drift.
/// </summary>
public class StreakStateTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly KillstreakFile Streaks = GameData.Load(DataDir).Killstreaks;

    /// <summary>The tracker before the machine: three sets, the state re-derived on every read.</summary>
    sealed class SetModel
    {
        readonly string[] _loadout = new string[KillstreakService.Slots];
        readonly HashSet<string> _earned = new(), _used = new(), _active = new();
        public SetModel() => Array.Fill(_loadout, "");
        public void Arm(IReadOnlyList<string> l)
        {
            var keep = _earned.Where(id => !_used.Contains(id) && _loadout.Contains(id)).ToList();
            Reset();
            foreach (var id in keep) _earned.Add(id);
            for (var i = 0; i < KillstreakService.Slots; i++) _loadout[i] = i < l.Count ? l[i] ?? "" : "";
        }
        public void Earned(string id) { if (!_used.Contains(id)) _earned.Add(id); }
        public void Used(string id) { _earned.Remove(id); _used.Add(id); _active.Add(id); }
        public void Ended(string id) => _active.Remove(id);
        public void Reset() { _earned.Clear(); _used.Clear(); _active.Clear(); }
        public void Death() { _used.Clear(); _active.Clear(); }
        public StreakSlotState State(int slot)
        {
            var id = _loadout[slot];
            if (id == "" || Streaks.Find(id) is null) return StreakSlotState.Empty;
            return _active.Contains(id) ? StreakSlotState.Active : _used.Contains(id) ? StreakSlotState.Spent : _earned.Contains(id) ? StreakSlotState.Ready : StreakSlotState.Progress;
        }
    }

    [Fact]
    public void TheTrackerAgreesWithTheSetModelOverARandomEventStream()
    {
        var ids = Streaks.Streaks.Select(s => s.Id).ToList();
        var pool = ids.Concat(new[] { "", "unknown" }).ToList();
        var rng = new Random(119);
        var t = new KillstreakTracker(Streaks);
        var m = new SetModel();
        for (var i = 0; i < 20_000; i++)
        {
            var id = pool[rng.Next(pool.Count)];
            switch (rng.Next(8))
            {
                case 0:
                    var l = new[] { pool[rng.Next(pool.Count)], pool[rng.Next(pool.Count)], pool[rng.Next(pool.Count)] };
                    t.Arm(l, 0f); m.Arm(l); break;
                case 1: t.OnEarned(id, 3); m.Earned(id); break;
                case 2: case 3: t.OnUsed(id); m.Used(id); break;
                case 4: t.OnEnded(id); m.Ended(id); break;
                case 5: if (rng.Next(6) == 0) { t.OnDeath(); m.Death(); } break;
                default: t.OnKill(); break;
            }
            for (var s = 0; s < KillstreakService.Slots; s++)
                Assert.True(m.State(s) == t.Slot(s).State, $"step {i}, slot {s}: model {m.State(s)}, tracker {t.Slot(s).State}");
        }
    }

    // ---- the per-use lifecycle ----

    sealed class Probe : IKillstreak
    {
        public string Id => "uav";
        public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string>();
        public Action<KillstreakUse>? OnActivate;
        public bool Accept = true;
        public bool Activate(KillstreakUse use) { OnActivate?.Invoke(use); return Accept; }
    }

    static (KillstreakService Svc, Probe Probe, List<(EventKind, byte[])> Sent) Service()
    {
        var file = KillstreakFile.Parse("""
            {"version":1,"streaks":[{"id":"uav","name":"UAV","kills_required":1,"unlock_level":0,"max_active":3,"control":"none"}],"streak_weights":{"uav":1}}
            """);
        var probe = new Probe();
        var sent = new List<(EventKind, byte[])>();
        var svc = new KillstreakService(file, new IKillstreak[] { probe }, (_, k, d) => sent.Add((k, d)), _ => { });
        return (svc, probe, sent);
    }

    [Fact]
    public void AUseIsActivatingInsideActivateThenOpenThenEnded()
    {
        var (svc, probe, sent) = Service();
        svc.Arm(1, new[] { "uav" }, 0);
        svc.OnKill(1, 2, false);
        KillstreakUse? seen = null;
        probe.OnActivate = u => { seen = u; Assert.Equal(KillstreakUse.UseState.Activating, u.State); Assert.True(u.IsOpen); };
        Assert.True(svc.Activate(1, 0, Vector3.Zero));
        Assert.Equal(KillstreakUse.UseState.Open, seen!.State);
        Assert.True(seen.IsOpen);
        seen.End(KillstreakEndReason.Finished);
        Assert.Equal(KillstreakUse.UseState.Ended, seen.State);
        Assert.False(seen.IsOpen);
        Assert.Equal(KillstreakEndReason.Finished, seen.EndReason);
        Assert.Contains(sent, s => s.Item1 == EventKind.StreakEnded);
    }

    [Fact]
    public void AnEndDuringActivateIsCompletedWhenActivateReturnsAndStillSendsUsedBeforeEnded()
    {
        var (svc, probe, sent) = Service();
        svc.Arm(1, new[] { "uav" }, 0);
        svc.OnKill(1, 2, false);
        probe.OnActivate = u => u.End(KillstreakEndReason.Finished);
        Assert.True(svc.Activate(1, 0, Vector3.Zero));
        var kinds = sent.Select(s => s.Item1).Where(k => k is EventKind.StreakUsed or EventKind.StreakEnded).ToList();
        Assert.Equal(new[] { EventKind.StreakUsed, EventKind.StreakEnded }, kinds);
        Assert.Equal(0, svc.ActiveCount(1, "uav"));
    }

    [Fact]
    public void ARefusedUseIsDroppedAndTheStreakStaysHeld()
    {
        var (svc, probe, sent) = Service();
        svc.Arm(1, new[] { "uav" }, 0);
        svc.OnKill(1, 2, false);
        probe.Accept = false;
        KillstreakUse? seen = null;
        probe.OnActivate = u => seen = u;
        Assert.False(svc.Activate(1, 0, Vector3.Zero));
        Assert.Equal(KillstreakUse.UseState.Refused, seen!.State);
        Assert.Equal(new[] { "uav" }, svc.Held(1));
        Assert.Equal(0, svc.ActiveCount(1, "uav"));
        Assert.DoesNotContain(sent, s => s.Item1 == EventKind.StreakUsed);
    }

    // ---- the per-life hold state ----

    [Fact]
    public void AStreakIsProgressThenEarnedThenUsedAndDeathStartsOver()
    {
        var (svc, probe, _) = Service();
        svc.Arm(1, new[] { "uav" }, 0);
        Assert.Equal(StreakHold.Progress, svc.HoldOf(1, "uav"));
        svc.OnKill(1, 2, false);
        Assert.Equal(StreakHold.Earned, svc.HoldOf(1, "uav"));
        Assert.True(svc.Activate(1, 0, Vector3.Zero));
        Assert.Equal(StreakHold.Used, svc.HoldOf(1, "uav"));
        Assert.False(svc.Activate(1, 1, Vector3.Zero)); // used this life
        svc.OnDeath(1);
        Assert.Equal(StreakHold.Progress, svc.HoldOf(1, "uav"));
        svc.OnKill(1, 2, false);
        Assert.Equal(StreakHold.Earned, svc.HoldOf(1, "uav"));
        svc.OnDeath(1);
        Assert.Equal(StreakHold.Earned, svc.HoldOf(1, "uav")); // earned survives a death (#414)
        svc.Reset();
        Assert.Equal(StreakHold.Progress, svc.HoldOf(1, "uav"));
        _ = probe;
    }

    [Fact]
    public void ARefusedActivationLeavesTheStreakEarned()
    {
        var (svc, probe, _) = Service();
        svc.Arm(1, new[] { "uav" }, 0);
        svc.OnKill(1, 2, false);
        probe.Accept = false;
        svc.Activate(1, 0, Vector3.Zero);
        Assert.Equal(StreakHold.Earned, svc.HoldOf(1, "uav"));
    }
}
