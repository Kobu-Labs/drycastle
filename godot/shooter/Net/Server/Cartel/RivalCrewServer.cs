using Godot;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The cartel's Rival crew on the server (#495, #496; GLOSSARY "Rival crew", "Gunman"): crews of Gang members on the Thieves' roster (<see cref="GangServer"/>: the hits, routes, poses, Gunmen's rounds),
/// each on rival_crew.json's numbers (<see cref="RivalCrewFile.GangOf"/>) and hostile to the players its owner names. A Job (Assaults, trap house defenders, a Rival dealer's guards) asks for a crew with
/// <see cref="NewCrew"/> and removes it with <see cref="Remove"/> when it ends; the admin's <c>/gunman</c> makes test crews, which go at the round's end.
/// </summary>
internal sealed class RivalCrewServer
{
    /// <summary>Most Gunmen one /gunman puts down, and the default.</summary>
    public const int MaxTestGunmen = ChatCommands.MaxTestGunmen, DefaultTestGunmen = 3;
    /// <summary>A test crew stands this far in front of the admin, spread over this arc either side of where they look.</summary>
    const float TestDistanceM = 12f, TestArcRad = 1.0f;
    /// <summary>A test member's point snapped to the navigation mesh is used when it moved no more than this.</summary>
    const float SnapM = 3f;

    readonly ServerContext _ctx;
    readonly GangServer _gangs;
    readonly List<ThiefGang> _tests = new();

    public RivalCrewServer(ServerContext ctx, ThievesFile data, GangServer gangs)
    {
        _ctx = ctx; _gangs = gangs;
        Config = data.RivalCrew.GangOf(data.Gang);
    }

    /// <summary>The Gang member numbers every Rival crew runs on.</summary>
    public GangConfig Config { get; }

    /// <summary>
    /// A new, empty Rival crew whose post is <paramref name="post"/>, attacking on sight the players <paramref name="hostileTo"/> names (asked at every look, so a Job's set may change), and anyone who hits a
    /// member. Add members with <see cref="ThiefGang.Add"/> (outdoors, on the navigation mesh) or <see cref="ThiefGang.AddPosted"/> (a spot and a facing, indoors).
    /// </summary>
    public ThiefGang NewCrew(NVec3 post, Func<ushort, bool> hostileTo)
    {
        var crew = _gangs.Roster.NewGang(post, Config, GangCrew.RivalCrew);
        crew.HostileTo = hostileTo;
        return crew;
    }

    /// <summary>The point on the navigation mesh nearest <paramref name="p"/> (itself without a mesh): where a member added outdoors stands.</summary>
    public NVec3 Snap(NVec3 p) => _gangs.Snap(p);

    /// <summary>Removes a crew and every member it still has, the dead too.</summary>
    public void Remove(ThiefGang crew) => _gangs.Roster.Remove(crew);

    /// <summary>A new round: the world's entities went with it; the test crews go too.</summary>
    public void RoundReset()
    {
        foreach (var crew in _tests) Remove(crew);
        _tests.Clear();
    }

    /// <summary><c>/gunman [n] [posted]</c>: n Gunmen (3 by default) in an arc in front of the admin, hostile to the admin only; posted, they hold their spots facing the admin. <c>/gunman clear</c> removes them.</summary>
    public void ChatGunman(ServerClient c, IReadOnlyList<string> args)
    {
        var n = DefaultTestGunmen;
        var posted = false;
        foreach (var a in args)
        {
            if (a == "clear") { var had = _tests.Sum(t => t.AliveCount); RoundReset(); _ctx.ChatTo(c, ChatLineKind.Info, $"Test Gunmen removed ({had} alive)."); return; }
            if (a == "posted") posted = true;
            else if (int.TryParse(a, out var k)) n = Math.Clamp(k, 1, MaxTestGunmen);
        }
        if (!_ctx.IsAlive(c.Id)) { _ctx.ChatTo(c, ChatLineKind.Error, "Only a living player can call Gunmen."); return; }
        foreach (var empty in _tests.Where(t => t.Count == 0).ToList()) { Remove(empty); _tests.Remove(empty); }
        var me = c.Body.GlobalPosition.ToNumerics();
        var yaw = c.Body.Yaw;
        var id = c.Id;
        var crew = NewCrew(me, p => p == id);
        _tests.Add(crew);
        var made = 0;
        for (var i = 0; i < n; i++)
        {
            var a = yaw + (n == 1 ? 0 : TestArcRad * (2f * i / (n - 1) - 1f));
            var raw = me + new NVec3(-MathF.Sin(a), 0, -MathF.Cos(a)) * TestDistanceM;
            var snapped = _gangs.Snap(raw);
            var at = NVec3.Distance(snapped, raw) <= SnapM ? snapped : raw;
            var facing = TurretMath.YawTo(at, me);
            var m = posted ? crew.AddPosted(GangMemberKind.Gunman, at, facing) : crew.Add(GangMemberKind.Gunman, at, facing);
            if (m != null) made++;
        }
        GD.Print($"server: /gunman by #{c.Id}: {made} Gunmen{(posted ? " posted" : "")}");
        if (made == 0) _ctx.ChatTo(c, ChatLineKind.Error, "No Gunman could be put down (the entity cap or the Gang members' hit ids are used up).");
        else _ctx.ChatTo(c, ChatLineKind.Info, $"{made} Gunm{(made == 1 ? "a" : "e")}n of the Rival crew {(posted ? "hold their spots" : "stand")} {TestDistanceM:F0} m in front of you, hostile to you only. /gunman clear removes them.");
    }
}
