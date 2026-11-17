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

    // --- the rocket launcher (#348) ---------------------------------------------------------------------------

    const int Gun = 2, Tube = 3;
    static readonly BotLauncher Loaded = new(Tube, 1, 2, 10f, 120f, 60f);

    /// <summary>A bot that carries a launcher; <paramref name="holding"/> is the slot in its hand (its magazine is that weapon's).</summary>
    static BotSelf Armed(BotLauncher l, int holding = Gun) =>
        holding == Tube ? new(1, Vector3.Zero, Stance.Stand, l.Magazine, 1, l.Reserve, false, false, Tube, Gun, l) : Self() with { Slot = Gun, GunSlot = Gun, Launcher = l };

    [Fact]
    public void ABotWithALoadedLauncherTakesItOutForATargetInRangeAndFiresIt()
    {
        var b = Brain();
        var w = new World { List = { Ahead(40) } };
        Assert.Equal(Tube, Run(b, w, 0.5, self: Armed(Loaded))[^1].Slot);
        Assert.True(Fired(Run(b, w, 1.5, 0.5, Armed(Loaded, Tube))));
    }

    [Fact]
    public void TheLauncherStaysAwayInsideItsOwnBlastAndWithAnEmptyTube()
    {
        Assert.All(Run(Brain(), new World { List = { Ahead(6) } }, 1.5, self: Armed(Loaded)), c => Assert.Equal(Gun, c.Slot));
        // an empty tube: once the enemy is seen the gun stays in the hand
        Assert.All(Run(Brain(), new World { List = { Ahead(40) } }, 1.5, self: Armed(Loaded with { Magazine = 0 })).Skip(10), c => Assert.Equal(Gun, c.Slot));
    }

    [Fact]
    public void AnEmptyTubeIsReloadedOnlyWhenNobodyIsInSight()
    {
        var empty = Loaded with { Magazine = 0 };
        var alone = Run(Brain(), new World(), 1, self: Armed(empty));
        Assert.All(alone, c => Assert.Equal(Tube, c.Slot));
        Assert.Contains(Run(Brain(), new World(), 1, self: Armed(empty, Tube)), c => c.Buttons.HasFlag(MoveButtons.Reload));
        // in a fight the empty tube is put away without a reload
        var fight = Run(Brain(), new World { List = { Ahead(40) } }, 1.5, self: Armed(empty, Tube));
        Assert.Equal(Gun, fight[^1].Slot);
        Assert.DoesNotContain(fight.Skip(10), c => c.Buttons.HasFlag(MoveButtons.Reload));
        Assert.All(Run(Brain(), new World(), 1, self: Armed(empty with { Reserve = 0 })), c => Assert.Equal(Gun, c.Slot)); // nothing left to load
    }

    [Fact]
    public void ARocketIsAimedAtTheFeetFarMoreSteadilyThanAGunAndLedAheadOfARunner()
    {
        // the worst angle off the wanted point while firing: the launcher's is a fraction of the gun's
        static float Worst(BotSelf self, float wantPitch)
        {
            var cmds = Run(Brain(), new World { List = { Ahead(40) } }, 6, self: self);
            return cmds.Skip(60).Max(c => MathF.Max(MathF.Abs(MathF.Atan2(MathF.Sin(c.Yaw), MathF.Cos(c.Yaw))), MathF.Abs(c.Pitch - wantPitch)));
        }
        var eye = MovementStep.EyeHeight(Stance.Stand);
        var gun = Worst(Self(), MathF.Atan2(1.15f - eye, 40f));
        var tube = Worst(Armed(Loaded, Tube), MathF.Atan2(0.2f - eye, 40f));
        Assert.True(tube < gun / 3f, $"launcher off by {tube:F4} rad, gun by {gun:F4} rad");
        Assert.True(tube <= BotTuning.Default.LauncherAimErrorDeg * MathF.PI / 180f + 1e-3f);

        // a target running to the bot's right (+X) at 6 m/s, 40 m away: the rocket takes 2/3 s, so the aim is about 4 m ahead of it
        var b = Brain();
        var w = new World { List = { Ahead(40) } };
        BotCommand last = default;
        for (var t = 0.0; t < 2; t += Dt)
        {
            w.List[0] = w.List[0] with { Feet = new Vector3((float)(6 * t), 0, -40) };
            last = b.Tick(t, Dt, Armed(Loaded, Tube), w);
        }
        var (direct, _) = BotBrain.Angles(new Vector3(0, eye, 0), w.List[0].Feet);
        var ahead = MathF.Atan2(MathF.Sin(direct - last.Yaw), MathF.Cos(direct - last.Yaw)); // positive: aimed to the right of the target
        Assert.InRange(ahead, 0.06f, 0.14f);
    }

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

    // ---- the route worker: a request goes out once, its answer comes back later ----

    /// <summary>A bot that asked for a route (goal 50 m ahead) and sent it to the worker under ticket 5.</summary>
    static (BotBrain B, World W, Vector3 Feet) Sent(double now = 0.5)
    {
        var b = Brain();
        var w = new World { GoalPos = new Vector3(0, 0, -50) };
        var feet = Vector3.Zero;
        Walk(b, w, now, ref feet);
        Assert.NotNull(b.PathRequest);
        Assert.False(b.PathInFlight);
        b.PathSent(5, now);
        return (b, w, feet);
    }

    [Fact]
    public void AnInFlightRequestIsNotSentAgainAndItsAnswerIsTakenOnce()
    {
        var (b, _, feet) = Sent();
        Assert.True(b.PathInFlight);
        Assert.Equal(5, b.PathTicket);
        Assert.False(b.AcceptPath(6, new[] { feet, new Vector3(0, 0, -50) }, feet, 1)); // another ticket: not its answer
        Assert.True(b.PathInFlight);
        Assert.True(b.AcceptPath(5, new[] { feet, new Vector3(0, 0, -50) }, feet, 1));
        Assert.False(b.PathInFlight);
        Assert.Null(b.PathRequest);
        Assert.False(b.AcceptPath(5, new[] { feet, new Vector3(0, 0, -50) }, feet, 1)); // the same answer twice
    }

    [Fact]
    public void AnAnswerFromBeforeANewLifeIsDropped()
    {
        var (b, _, feet) = Sent();
        b.Reset(0, 1);
        Assert.False(b.PathInFlight);
        Assert.False(b.AcceptPath(5, new[] { feet, new Vector3(0, 0, -50) }, feet, 1));
        Assert.Null(b.PathRequest);
    }

    [Fact]
    public void AnAnswerForAGoalThatHasMovedAwayIsDroppedAndTheBotAsksAgain()
    {
        var (b, w, feet) = Sent();
        w.GoalPos = new Vector3(60, 0, -50); // 60 m aside: far more than the repath distance
        Walk(b, w, 0.2, ref feet, start: 0.5);
        Assert.False(b.AcceptPath(5, new[] { feet, new Vector3(0, 0, -50) }, feet, 1));
        Assert.False(b.PathInFlight); // free to send again
        Assert.NotNull(b.PathRequest);
        Assert.Equal(new Vector3(60, 0, -50), b.PathRequest!.Value.To);
    }

    [Fact]
    public void AnAnswerForABotThatNoLongerWantsARouteIsDropped()
    {
        var (b, w, feet) = Sent();
        w.GoalPos = null; // alone: nowhere to go
        Walk(b, w, 0.2, ref feet, start: 0.5);
        Assert.Null(b.PathRequest);
        Assert.False(b.AcceptPath(5, new[] { feet, new Vector3(0, 0, -50) }, feet, 1));
        Assert.False(b.PathInFlight);
    }

    [Fact]
    public void ARequestTheWorkerNeverAnswersIsGivenUp()
    {
        var (b, w, feet) = Sent();
        Walk(b, w, BotBrain.AskGiveUpS - 1, ref feet, start: 0.5);
        Assert.True(b.PathInFlight);
        Walk(b, w, 2, ref feet, start: 0.5 + BotBrain.AskGiveUpS - 1);
        Assert.False(b.PathInFlight);
        Assert.NotNull(b.PathRequest); // it can ask again
    }

    [Fact]
    public void ALateAnswerStartsAtTheNextPointWhenTheBotHasRunPastTheFirst()
    {
        var (b, w, _) = Sent();
        var feet = new Vector3(0, 0, -6); // ran 6 m ahead while the worker thought
        Assert.True(b.AcceptPath(5, new[] { Vector3.Zero, new Vector3(0, 0, -20), new Vector3(0, 0, -50) }, feet, 1));
        var cmds = Walk(b, w, 0.3, ref feet, start: 1);
        Assert.Contains(cmds, c => c.MoveY > 0.5f); // straight on: not turning back for the point it passed
        Assert.True(feet.Z < -6f);
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
