using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// Who works for the Cartel (docs/cartel.md "Membership and rank"; ADR 0045): joining and quitting, the Respect a member is granted, and each player's standing
/// (<see cref="EventKind.CartelStatus"/>). Membership and Respect live on the Profile and are written through the store's write queue; a guest cannot join, a bot never does,
/// a sandbox player earns nothing. A rank-up goes to everyone (<see cref="EventKind.CartelRankUp"/> and a chat line). The Contact's reach and the Jobs are <see cref="CartelServer"/>'s.
/// </summary>
internal sealed class CartelMembers
{
    readonly ServerContext _ctx;
    readonly CartelFile _data;
    readonly IProgressionStore? _store;

    public CartelMembers(ServerContext ctx, CartelFile data, IProgressionStore? store)
    {
        _ctx = ctx; _data = data; _store = store;
    }

    /// <summary>Joins the player (saved, their standing sent, told in the chat), or why not.</summary>
    public CartelJoinRefusal Join(ServerClient c)
    {
        var why = CartelRules.CanJoin(c.IsBot, c.Profile?.Persist == true, c.Profile?.Cartel);
        if (why != CartelJoinRefusal.None) return why;
        var p = c.Profile!;
        p.Cartel.Join();
        _ = _store?.Profiles.SetCartelMembershipAsync(p.DbId, p.Cartel.Membership);
        SendStatus(c);
        _ctx.ChatTo(c, ChatLineKind.Info, $"You work for the Cartel: {RankName(p.Cartel)}, {p.Cartel.Respect} Respect.");
        GD.Print($"server: {c.Name} #{c.Id} joined the Cartel ({p.Cartel.Respect} Respect)");
        return CartelJoinRefusal.None;
    }

    /// <summary>The player quits (rank and Respect kept for a rejoin), or <see cref="CartelJoinRefusal.NotMember"/>. The Job they lead is the caller's to fail.</summary>
    public CartelJoinRefusal Quit(ServerClient c)
    {
        if (c.Profile is not { Cartel.IsMember: true } p) return CartelJoinRefusal.NotMember;
        p.Cartel.Quit();
        if (p.Persist) _ = _store?.Profiles.SetCartelMembershipAsync(p.DbId, p.Cartel.Membership);
        SendStatus(c);
        _ctx.ChatTo(c, ChatLineKind.Info, $"You quit the Cartel. Your rank ({RankName(p.Cartel)}) and Respect wait for your return.");
        GD.Print($"server: {c.Name} #{c.Id} quit the Cartel");
        return CartelJoinRefusal.None;
    }

    /// <summary>Adds Respect to a member, writes it through the store's queue, tells them and, on a rank-up, everyone. What was added.</summary>
    public int Grant(ServerClient c, int amount)
    {
        var p = c.Profile!;
        var change = p.Cartel.AddRespect(amount, _data);
        if (change.Added > 0 && p.Persist) _ = _store?.Profiles.AddRespectAsync(p.DbId, change.Added);
        SendStatus(c, change.RankedUp);
        if (change.RankedUp)
        {
            _ctx.Broadcast(EventKind.CartelRankUp, CartelCodec.EncodeRankUp(c.Id, (byte)change.After));
            _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} is now a {_data.Ranks[change.After].Name} of the Cartel.");
            GD.Print($"server: {c.Name} #{c.Id} rose to {_data.Ranks[change.After].Id}");
        }
        return change.Added;
    }

    /// <summary>The player's standing, to them (a human with a Profile).</summary>
    public void SendStatus(ServerClient c, bool rankedUp = false)
    {
        if (c.IsBot || c.Profile is not { } p) return;
        var flags = CartelStatusFlags.None;
        if (!p.Persist) flags |= CartelStatusFlags.Guest;
        if (c.Sandbox) flags |= CartelStatusFlags.Sandbox;
        if (rankedUp) flags |= CartelStatusFlags.RankedUp;
        _ctx.SendEvent(c, EventKind.CartelStatus, CartelCodec.EncodeStatus(new CartelStatusMsg(p.Cartel.Membership, p.Cartel.Respect, (byte)p.Cartel.Rank(_data), flags)));
    }

    public string RankName(CartelStanding s) => _data.Ranks[s.Rank(_data)].Name;

    /// <summary>Why the player earns no Respect from a Job now; None when they earn.</summary>
    public static NoRespectReason NoRespect(ServerClient c) => CartelRules.NoRespect(c.IsBot, c.Profile?.Persist == true, c.Sandbox, c.Profile?.Cartel);

    public static bool Earns(ServerClient c) => NoRespect(c) == NoRespectReason.None;

    /// <summary>
    /// What a Job message says about the player (all but the leader bit): they earn; they are not in the Cartel but could join (a stored Profile); they are a guest, who cannot.
    /// Joining or quitting midway, or a sandbox toggle, changes them, and the Job goes to the player again (<see cref="ShooterCore.Jobs.JobFeed"/>).
    /// </summary>
    public static CartelJobFlags FlagsOf(ServerClient c)
    {
        var flags = CartelJobFlags.None;
        if (Earns(c)) flags |= CartelJobFlags.Earns;
        if (c.Profile?.Persist != true) flags |= CartelJobFlags.Guest;
        else if (!c.Profile.Cartel.IsMember) flags |= CartelJobFlags.NotMember;
        return flags;
    }
}
