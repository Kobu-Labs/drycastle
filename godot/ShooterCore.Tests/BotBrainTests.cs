using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The bot brain (#214): what it notices, when it fires, how it aims, and what it leaves alone.</summary>
public class BotBrainTests
{
    const float Dt = 1f / 30f;

    sealed class World : IBotWorld
    {
        public List<BotContact> List = new();
        public bool Blocked;
        public int Rays;
        public IReadOnlyList<BotContact> Enemies(ushort self) => List;
        public bool Clear(Vector3 from, Vector3 to) { Rays++; return !Blocked; }
        public Vector3? GoalPos;
        public Vector3? Goal(ushort self) => GoalPos;
    }

    static BotSelf Self(int magazine = 30, bool reloading = false, bool fullAuto = true) =>
        new(1, Vector3.Zero, Stance.Stand, magazine, 30, 90, reloading, fullAuto);

    /// <summary>An enemy <paramref name="metres"/> ahead of a bot that faces yaw 0 (-Z).</summary>
    static BotContact Ahead(float metres, ushort id = 2, bool isProtected = false) => new(id, new Vector3(0, 0, -metres), Stance.Stand, isProtected);

    static BotBrain Brain(float yaw = 0)
    {
        var b = new BotBrain(BotTuning.Default, 7);
        b.Reset(yaw, 0);
        return b;
    }

    /// <summary>Runs the brain for <paramref name="seconds"/> from <paramref name="start"/>; returns every command.</summary>
    static List<BotCommand> Run(BotBrain b, World w, double seconds, double start = 0, BotSelf? self = null)
    {
        var s = self ?? Self();
        var cmds = new List<BotCommand>();
        for (var t = start; t < start + seconds; t += Dt) cmds.Add(b.Tick(t, Dt, s, w));
        return cmds;
    }

    static bool Fired(IEnumerable<BotCommand> cmds) => cmds.Any(c => c.Buttons.HasFlag(MoveButtons.Fire));

    [Fact]
    public void WithNobodyAroundItLooksAboutAndNeverFires()
    {
        var b = Brain();
        var cmds = Run(b, new World(), 3);
        Assert.False(Fired(cmds));
        Assert.Null(b.Target);
        Assert.NotEqual(cmds[0].Yaw, cmds[^1].Yaw); // it turns to scan
        Assert.All(cmds, c => Assert.True(c.MoveX == 0 && c.MoveY == 0)); // this version stands
    }

    [Fact]
    public void AnEnemyInViewIsEngagedOnlyAfterTheReactionTime()
    {
        var b = Brain();
        var w = new World { List = { Ahead(30) } };
        var cmds = Run(b, w, 2);
        Assert.Equal((ushort)2, b.Target);
        var first = cmds.FindIndex(c => c.Buttons.HasFlag(MoveButtons.Fire));
        Assert.True(first >= 0, "the bot fires at an enemy in plain view");
        // sight comes at the first look (within one think interval), the shot no sooner than the reaction time after it
        Assert.True(first * Dt >= BotTuning.Default.ReactionS - Dt, $"fired after {first * Dt:F2} s");
    }

    [Fact]
    public void ItFiresInBurstsNotOneLongPull()
    {
        var cmds = Run(Brain(), new World { List = { Ahead(30) } }, 4);
        var afterFirst = cmds.SkipWhile(c => !c.Buttons.HasFlag(MoveButtons.Fire)).ToList();
        Assert.Contains(afterFirst, c => !c.Buttons.HasFlag(MoveButtons.Fire)); // a pause follows the first burst
        Assert.True(afterFirst.Count(c => c.Buttons.HasFlag(MoveButtons.Fire)) > 15, "and more bursts follow");
    }

    [Fact]
    public void AnEnemyBehindAWallIsNeverSeen()
    {
        var b = Brain();
        var cmds = Run(b, new World { List = { Ahead(30) }, Blocked = true }, 3);
        Assert.False(Fired(cmds));
        Assert.Null(b.Target);
    }

    [Fact]
    public void AnEnemyBehindItIsNotSeenUntilTheScanComesRound()
    {
        var b = Brain();
        var w = new World { List = { new BotContact(2, new Vector3(0, 0, 40), Stance.Stand, false) } }; // straight behind
        Assert.False(Fired(Run(b, w, 0.5)));
        Assert.Null(b.Target);
        Run(b, w, 6, start: 0.5); // 40 degrees a second: half a turn takes 4.5 s
        Assert.Equal((ushort)2, b.Target);
    }

    [Fact]
    public void AnEnemyBehindItButVeryCloseIsNoticed()
    {
        var b = Brain();
        Run(b, new World { List = { new BotContact(2, new Vector3(0, 0, 3), Stance.Stand, false) } }, 0.5);
        Assert.Equal((ushort)2, b.Target);
    }

    [Fact]
    public void AHitFromBehindTurnsItRound()
    {
        var b = Brain();
        var behind = new Vector3(0, 0, 40);
        var w = new World { List = { new BotContact(2, behind, Stance.Stand, false) } };
        b.Alert(behind, 0);
        Run(b, w, 1.5); // 240 degrees a second covers the half turn well inside the alert
        Assert.Equal((ushort)2, b.Target);
    }

    [Fact]
    public void TheAimTurnsAtALimitedRateAndEndsOnTheTarget()
    {
        var b = Brain();
        var side = new Vector3(-30, 0, 0); // 90 degrees to the left
        var w = new World { List = { new BotContact(2, side, Stance.Stand, false) } };
        b.Alert(side, 0); // out of the view cone: the hit direction brings it round
        var cmds = Run(b, w, 2);
        var maxStep = BotTuning.Default.TurnRateDegPerS * MathF.PI / 180f * Dt + 1e-4f;
        for (var i = 1; i < cmds.Count; i++)
        {
            var d = MathF.Abs(MathF.IEEERemainder(cmds[i].Yaw - cmds[i - 1].Yaw, MathF.Tau));
            Assert.True(d <= maxStep, $"turned {d} rad in one tick");
        }
        var want = BotBrain.Angles(new Vector3(0, MoveConsts.EyeHeight, 0), BotBrain.AimPoint(w.List[0])).Yaw;
        var off = MathF.Abs(MathF.IEEERemainder(cmds[^1].Yaw - want, MathF.Tau)) * 180f / MathF.PI;
        Assert.True(off <= BotTuning.Default.AimErrorDeg + 0.1f, $"aim is {off:F1} degrees off after 2 s");
    }

    [Fact]
    public void ASpawnProtectedEnemyIsLeftAlone()
    {
        var b = Brain();
        Assert.False(Fired(Run(b, new World { List = { Ahead(30, isProtected: true) } }, 2)));
        Assert.Null(b.Target);
    }

    [Fact]
    public void ItStopsFiringWhenTheTargetGoesOutOfSightAndForgetsIt()
    {
        var b = Brain();
        var w = new World { List = { Ahead(30) } };
        Assert.True(Fired(Run(b, w, 2)));
        w.Blocked = true;
        var after = Run(b, w, 3, start: 2);
        Assert.False(Fired(after.Skip(6))); // within one think interval the trigger is released
        Assert.Null(b.Target);              // and after the memory time the target is dropped
    }

    [Fact]
    public void OfTwoEnemiesInViewItTakesTheNearer()
    {
        var b = Brain();
        Run(b, new World { List = { Ahead(60, id: 2), Ahead(20, id: 3) } }, 1);
        Assert.Equal((ushort)3, b.Target);
    }

    [Fact]
    public void AnEmptyMagazineIsReloadedNotFired()
    {
        var cmds = Run(Brain(), new World { List = { Ahead(30) } }, 2, self: Self(magazine: 0));
        Assert.False(Fired(cmds));
        Assert.Contains(cmds, c => c.Buttons.HasFlag(MoveButtons.Reload));
    }

    [Fact]
    public void ASemiAutomaticWeaponIsTappedNotHeld()
    {
        var cmds = Run(Brain(), new World { List = { Ahead(30) } }, 3, self: Self(fullAuto: false));
        Assert.True(Fired(cmds));
        for (var i = 1; i < cmds.Count; i++)
            Assert.False(cmds[i].Buttons.HasFlag(MoveButtons.Fire) && cmds[i - 1].Buttons.HasFlag(MoveButtons.Fire), "fire held two ticks running");
    }

    [Fact]
    public void SightRaysAreCastAtTheThinkRateNotEveryTick()
    {
        var w = new World { List = { Ahead(30) } };
        Run(Brain(), w, 3);
        Assert.InRange(w.Rays, 15, 25); // one enemy, a look every 0.15 s for 3 s
    }

    // ---- moving ----

    /// <summary>
    /// Runs the brain and moves the body by its commands as the server would on open ground (walk 5 m/s, sprint 8 m/s, no
    /// collision). <paramref name="frozen"/> keeps the body where it is: something is in the way.
    /// </summary>
    static List<BotCommand> Walk(BotBrain b, World w, double seconds, ref Vector3 feet, double start = 0, bool frozen = false, Action<BotBrain, double, Vector3>? eachTick = null)
    {
        var cmds = new List<BotCommand>();
        for (var t = start; t < start + seconds; t += Dt)
        {
            eachTick?.Invoke(b, t, feet);
            var c = b.Tick(t, Dt, Self() with { Feet = feet }, w);
            cmds.Add(c);
            if (frozen) continue;
            var (sin, cos) = MathF.SinCos(c.Yaw);
            var wish = new Vector3(cos, 0, -sin) * c.MoveX + new Vector3(-sin, 0, -cos) * c.MoveY;
            if (wish.Length() > 1) wish = Vector3.Normalize(wish);
            feet += wish * (c.Buttons.HasFlag(MoveButtons.Sprint) ? MoveConsts.SprintSpeed : MoveConsts.WalkSpeed) * Dt;
        }
        return cmds;
    }

    /// <summary>Answers a route request with the given points, like the host does.</summary>
    static Action<BotBrain, double, Vector3> Answer(params Vector3[] route) => (b, t, _) =>
    {
        if (b.PathRequest is { } r) b.SetPath(route, r.To, t);
    };

    [Fact]
    public void WithAGoalItAsksForARouteFromWhereItStands()
    {
        var b = Brain();
        var feet = new Vector3(2, 0, 3);
        Walk(b, new World { GoalPos = new Vector3(0, 0, -50) }, 0.5, ref feet);
        Assert.NotNull(b.PathRequest);
        Assert.Equal(new Vector3(0, 0, -50), b.PathRequest!.Value.To);
        Assert.Equal(feet, b.PathRequest.Value.From);
    }

    [Fact]
    public void WithNoGoalItAsksForNothingAndStands()
    {
        var b = Brain();
        var feet = Vector3.Zero;
        var cmds = Walk(b, new World(), 2, ref feet);
        Assert.Null(b.PathRequest);
        Assert.Equal(Vector3.Zero, feet);
        Assert.All(cmds, c => Assert.Equal(0f, c.MoveY));
    }

    [Fact]
    public void ItWalksARouteRoundACornerAndStopsWhenItIsThere()
    {
        var goal = new Vector3(40, 0, -40);
        var corner = new Vector3(0, 0, -40);
        var w = new World { GoalPos = goal };
        var feet = Vector3.Zero;
        var nearest = float.MaxValue;
        // like the host, each answer is a route from where the bot stands: by the corner until it has been passed
        var cmds = Walk(Brain(), w, 20, ref feet, eachTick: (b, t, f) =>
        {
            nearest = MathF.Min(nearest, Vector3.Distance(f, corner));
            if (b.PathRequest is { } r) b.SetPath(nearest > 1f ? new[] { f, corner, goal } : new[] { f, goal }, r.To, t);
        });
        Assert.True(Vector3.Distance(feet, goal) <= BotTuning.Default.ArriveM + 0.5f, $"ended {Vector3.Distance(feet, goal):F1} m from the goal");
        Assert.True(nearest <= 1.5f, $"passed {nearest:F1} m from the corner"); // by the corner, not straight across
        Assert.Contains(cmds, c => c.Buttons.HasFlag(MoveButtons.Sprint));      // it runs while far away
        Assert.Equal(0f, cmds[^1].MoveY);                                       // and stands once there
    }

    [Fact]
    public void ItStopsWalkingToShootAnEnemyThatComesIntoView()
    {
        var b = Brain();
        var goal = new Vector3(0, 0, -200);
        var w = new World { GoalPos = goal };
        var feet = Vector3.Zero;
        Walk(b, w, 2, ref feet, eachTick: Answer(Vector3.Zero, goal));
        Assert.True(feet.Z < -5, "it was walking");
        w.List.Add(new BotContact(2, feet + new Vector3(0, 0, -30), Stance.Stand, false));
        var at = feet;
        var cmds = Walk(b, w, 2, ref feet, start: 2, eachTick: Answer(Vector3.Zero, goal));
        Assert.True(Fired(cmds));
        Assert.True(Vector3.Distance(at, feet) < 1.5f, $"kept walking {Vector3.Distance(at, feet):F1} m while fighting");
    }

    [Fact]
    public void AFarEnemyIsShotAtWhileItKeepsClosingInAndItHoldsAtItsRange()
    {
        var b = Brain();
        var enemy = new Vector3(0, 0, -90);
        var w = new World { GoalPos = enemy, List = { new BotContact(2, enemy, Stance.Stand, false) } };
        var feet = Vector3.Zero;
        var cmds = Walk(b, w, 4, ref feet, eachTick: (bb, t, f) => { if (bb.PathRequest is { } r) bb.SetPath(new[] { f, enemy }, r.To, t); });
        Assert.True(Fired(cmds), "it shoots on the way");
        Assert.True(feet.Z < -10, $"it closed in only {-feet.Z:F1} m in 4 s");
        Assert.DoesNotContain(cmds, c => c.Buttons.HasFlag(MoveButtons.Fire) && c.Buttons.HasFlag(MoveButtons.Sprint)); // no sprint with the trigger down
        Walk(b, w, 12, ref feet, start: 4, eachTick: (bb, t, f) => { if (bb.PathRequest is { } r) bb.SetPath(new[] { f, enemy }, r.To, t); });
        var left = Vector3.Distance(feet, enemy);
        Assert.InRange(left, BotTuning.Default.HoldRangeM - 3f, BotTuning.Default.HoldRangeM + 1f); // then it stands and fights
    }

    [Fact]
    public void WithTheFightToOneSideItStrafesAlongItsRouteAndKeepsItsAim()
    {
        var b = Brain();
        var enemy = new Vector3(-80, 0, 0);   // to the left
        var goal = new Vector3(0, 0, -200);   // the route runs straight ahead
        var w = new World { GoalPos = goal, List = { new BotContact(2, enemy, Stance.Stand, false) } };
        var feet = Vector3.Zero;
        b.Alert(enemy, 0);
        Walk(b, w, 1.5, ref feet, eachTick: Answer(Vector3.Zero, goal)); // it turns to the enemy and engages
        Assert.Equal((ushort)2, b.Target);
        var from = feet;
        var cmds = Walk(b, w, 2, ref feet, start: 1.5, eachTick: (bb, t, f) => { if (bb.PathRequest is { } r) bb.SetPath(new[] { f, goal }, r.To, t); });
        Assert.True(from.Z - feet.Z > 5, $"moved {from.Z - feet.Z:F1} m along the route");
        Assert.True(MathF.Abs(feet.X - from.X) < 1.5f, "and not toward the enemy");
        Assert.Contains(cmds, c => c.MoveX > 0.5f); // the route is to the right of where it looks: a strafe
    }

    [Fact]
    public void MakingNoHeadwayItJumpsAndSidestepsThenAsksForANewRoute()
    {
        var goal = new Vector3(0, 0, -100);
        var w = new World { GoalPos = goal };
        var feet = Vector3.Zero;
        var answered = 0;
        var cmds = Walk(Brain(), w, 6, ref feet, frozen: true, eachTick: (b, t, f) => { if (b.PathRequest is { } r) { answered++; b.SetPath(new[] { Vector3.Zero, goal }, r.To, t); } });
        Assert.Contains(cmds, c => c.Buttons.HasFlag(MoveButtons.Jump));
        Assert.Contains(cmds, c => c.MoveX != 0);
        Assert.True(answered >= 2, $"asked for a route {answered} times"); // the first, and another after three failed escapes
    }

    [Fact]
    public void AGoalThatMovesAwayBringsANewRoute()
    {
        var b = Brain();
        var w = new World { GoalPos = new Vector3(0, 0, -100) };
        var feet = Vector3.Zero;
        var asked = new List<Vector3>();
        Action<BotBrain, double, Vector3> answer = (bb, t, f) => { if (bb.PathRequest is { } r) { asked.Add(r.To); bb.SetPath(new[] { f, r.To }, r.To, t); } };
        Walk(b, w, 1.5, ref feet, eachTick: answer);
        w.GoalPos = new Vector3(60, 0, -100); // the action moved 60 m
        Walk(b, w, 2, ref feet, start: 1.5, eachTick: answer);
        Assert.Equal(new Vector3(60, 0, -100), asked[^1]);
        Assert.True(asked.Count <= 4, $"asked {asked.Count} times in 3.5 s"); // not every tick
    }

    [Fact]
    public void TheSameSeedGivesTheSameBot()
    {
        var a = Run(Brain(), new World { List = { Ahead(30) } }, 3);
        var b = Run(Brain(), new World { List = { Ahead(30) } }, 3);
        Assert.Equal(a, b);
    }
}
