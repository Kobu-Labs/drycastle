using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Cartel on the server (docs/cartel.md, ADR 0045, #494): joining and quitting, the Jobs and what they pay. A player asks at the Contact (<see cref="EventKind.CartelRequest"/>:
/// join, quit, start a Job); the rules are ShooterCore's (<see cref="CartelRules"/>, <see cref="CartelStanding"/>, <see cref="JobBook"/>). Membership and Respect live on the Profile
/// and are written through the store's write queue; a guest cannot join, a bot never does, a sandbox player runs Jobs and earns nothing. Who runs a Job is
/// <see cref="IJobParties"/>'s (ffa: solo; tdm: the Squad and its leader). Kinds of Job plug in with <see cref="Register"/>; the test kind is built in. Each player is told their
/// standing (<see cref="EventKind.CartelStatus"/>) and their Job (<see cref="EventKind.CartelJob"/>); a rank-up goes to everyone (<see cref="EventKind.CartelRankUp"/> and a chat line).
/// </summary>
internal sealed class CartelServer
{
    /// <summary>Most Respect one <c>/cartel respect</c> adds.</summary>
    const int MaxGrant = 10_000_000;

    readonly ServerContext _ctx;
    readonly CartelFile _data;
    readonly IProgressionStore? _store;
    readonly IJobParties _parties;
    readonly IContactSite _contact;
    readonly JobBook _book = new();
    readonly List<JobEnded> _ended = new();
    /// <summary>What each running Job's party was last told: its leader and members (a change is sent again; a member who left is told "no job").</summary>
    readonly Dictionary<int, (ushort Leader, ushort[] Members)> _told = new();
    /// <summary>What each running Job's party was last sent of its objective (<see cref="EventKind.CartelObjective"/>): the line and the marker. A party change sends it again to all.</summary>
    readonly Dictionary<int, (string Line, System.Numerics.Vector3? Marker)> _objectiveSent = new();

    /// <param name="parties">Who runs a Job (<see cref="MatchJobParties"/>).</param>
    /// <param name="contact">Where the Contact stands; <see cref="MapContactSite"/>, the map's placed Contact.</param>
    public CartelServer(ServerContext ctx, CartelFile data, IProgressionStore? store, IJobParties parties, IContactSite contact)
    {
        _ctx = ctx; _data = data; _store = store; _parties = parties; _contact = contact;
        Register(new TestJobKind(data));
    }

    /// <summary>The running Jobs and their reservations (a kind's server part claims its drop points here).</summary>
    public JobBook Jobs => _book;

    /// <summary>Adds a kind of Job (Delivery, the heists, Hit: their own packages).</summary>
    public void Register(IJobKind kind) => _book.Register(kind);

    /// <summary>Once a tick: the Jobs step (objectives, timers, leaders); what ended is paid; a party that changed is told.</summary>
    public void Tick(float dt)
    {
        if (_book.Running.Count == 0) return;
        _book.Tick(dt, _parties, _ended);
        Settle();
        TellParties();
        TellObjectives();
    }

    /// <summary>The player stands in reach of the map's Contact (false on a map without one): his panel has the interact key there, not the plantation's hold.</summary>
    public bool ContactInReach(ServerClient c) => _contact.Present && AtContact(c);

    /// <summary>A player died: the Job they lead (solo, or as the Squad's leader) fails.</summary>
    public void OnDied(ushort victim)
    {
        if (_book.Running.Count == 0) return;
        _book.PlayerDied(victim, _parties, _ended);
        Settle();
    }

    /// <summary>A round reset: every Job fails.</summary>
    public void RoundReset()
    {
        _book.FailAll(JobFailReason.RoundReset, _parties, _ended);
        Settle();
    }

    /// <summary>Join: the player's standing.</summary>
    public void SendOnJoin(ServerClient c) => SendStatus(c);

    /// <summary>A request from the Contact.</summary>
    public void OnRequest(ServerClient c, byte[] payload)
    {
        if (!CartelCodec.TryDecodeRequest(payload, out var r)) return;
        switch (r.Action)
        {
            case CartelAction.Join: Join(c, admin: false); break;
            case CartelAction.Quit: Quit(c, admin: false); break;
            case CartelAction.StartJob: StartJob(c, r.Kind, r.Tier, admin: false); break;
        }
    }

    bool AtContact(ServerClient c)
    {
        var p = c.Body.GlobalPosition;
        return _contact.InReach(new System.Numerics.Vector3(p.X, p.Y, p.Z));
    }

    /// <summary>False (and the player told) when the map has no Contact, or the player is out of his reach (the admin's command skips the reach).</summary>
    bool ContactAnswers(ServerClient c, bool admin)
    {
        if (!_contact.Present) { _ctx.ChatTo(c, ChatLineKind.Error, "This map has no Cartel."); return false; }
        if (admin || AtContact(c)) return true;
        _ctx.ChatTo(c, ChatLineKind.Error, "Talk to the Contact for that.");
        return false;
    }

    void Join(ServerClient c, bool admin)
    {
        if (!ContactAnswers(c, admin)) return;
        var why = CartelRules.CanJoin(c.IsBot, c.Profile?.Persist == true, c.Profile?.Cartel);
        if (why != CartelJoinRefusal.None) { _ctx.ChatTo(c, ChatLineKind.Error, CartelRules.Words(why)); return; }
        var p = c.Profile!;
        p.Cartel.Join();
        _ = _store?.Profiles.SetCartelMembershipAsync(p.DbId, p.Cartel.Membership);
        SendStatus(c);
        _ctx.ChatTo(c, ChatLineKind.Info, $"You work for the Cartel: {RankName(p.Cartel)}, {p.Cartel.Respect} Respect.");
        GD.Print($"server: {c.Name} #{c.Id} joined the Cartel ({p.Cartel.Respect} Respect)");
    }

    void Quit(ServerClient c, bool admin)
    {
        if (!ContactAnswers(c, admin)) return;
        if (c.Profile is not { Cartel.IsMember: true } p) { _ctx.ChatTo(c, ChatLineKind.Error, "You do not work for the Cartel."); return; }
        p.Cartel.Quit();
        if (p.Persist) _ = _store?.Profiles.SetCartelMembershipAsync(p.DbId, p.Cartel.Membership);
        _book.LeaderQuit(c.Id, _parties, _ended); // the Job they lead fails
        Settle();
        SendStatus(c);
        _ctx.ChatTo(c, ChatLineKind.Info, $"You quit the Cartel. Your rank ({RankName(p.Cartel)}) and Respect wait for your return.");
        GD.Print($"server: {c.Name} #{c.Id} quit the Cartel");
    }

    void StartJob(ServerClient c, string kindId, int tier, bool admin)
    {
        if (!ContactAnswers(c, admin)) return;
        var kind = _book.Kind(kindId);
        var owner = _parties.OwnerOf(c.Id);
        var members = _parties.MembersOf(owner);
        var standing = c.Profile?.Cartel;
        var facts = new JobStartFacts(c.IsBot, standing?.IsMember == true, _parties.LeaderOf(owner) == c.Id, kind != null, tier, standing?.Rank(_data) ?? 0,
            _ctx.Match.Phase == MatchPhase.Running, _ctx.IsAlive(c.Id), AtContact(c), _book.OfOwner(owner) != null, members.Any(m => _book.Involving(m, _parties) != null), admin);
        var why = CartelRules.CanStart(_data, facts);
        if (why != JobStartRefusal.None) { _ctx.ChatTo(c, ChatLineKind.Error, CartelRules.Words(why, _data, tier)); return; }
        if (_book.Start(CartelRules.Giver, kind!, tier, owner, c.Id, out var refusal) is not { } job) { _ctx.ChatTo(c, ChatLineKind.Error, refusal ?? "The job cannot start now."); return; }
        TellParties();
        TellObjectives();
        foreach (var m in members)
        {
            if (!_ctx.Players.TryGetValue(m, out var mc) || mc.IsBot) continue;
            _ctx.ChatTo(mc, ChatLineKind.Info, $"{c.Name} started the job {kind!.Name} (tier {tier}): {job.Seconds:F0} s.");
            if (mc.Profile?.Cartel.IsMember != true) _ctx.ChatTo(mc, ChatLineKind.Info, "Join the Cartel at the Contact to earn Respect from jobs.");
        }
        GD.Print($"server: {c.Name} #{c.Id} started {job}{(admin ? " (admin)" : "")}");
    }

    /// <summary>Pays and tells what ended since the last call: on success each member of the party at that moment who earns gets the reward; everyone in it hears the end.</summary>
    void Settle()
    {
        if (_ended.Count == 0) return;
        foreach (var e in _ended)
        {
            var job = e.Job;
            _told.Remove(job.Id);
            _objectiveSent.Remove(job.Id);
            var reward = e.Succeeded ? CartelRules.Reward(_data, job) : 0;
            foreach (var m in e.Members)
            {
                if (!_ctx.Players.TryGetValue(m, out var mc) || mc.IsBot) continue;
                var paid = 0;
                if (e.Succeeded && Earns(mc)) paid = Grant(mc, reward);
                _ctx.SendEvent(mc, EventKind.CartelJob, CartelCodec.EncodeJob(JobMsg(job, mc, paid)));
                _ctx.ChatTo(mc, ChatLineKind.Info, !e.Succeeded ? $"Job failed: {CartelRules.Words(job.Reason)}."
                    : paid > 0 ? $"Job done: +{paid} Respect." : $"Job done. You earn no Respect: {NoRespectWhy(mc)}");
            }
            GD.Print($"server: {job} ended{(e.Succeeded ? $", {reward} Respect each to {e.Members.Count(m => _ctx.Players.TryGetValue(m, out var o) && Earns(o))} member(s)" : $": {job.Reason}")}");
        }
        _ended.Clear();
    }

    static string NoRespectWhy(ServerClient c) =>
        c.Profile?.Persist != true ? "guests have no saved profile." : c.Sandbox ? "sandbox mode earns nothing." : "join the Cartel at the Contact to earn it.";

    static bool Earns(ServerClient c) => CartelRules.Earns(c.IsBot, c.Profile?.Persist == true, c.Sandbox, c.Profile?.Cartel);

    /// <summary>Adds Respect to a member, writes it through the store's queue, tells them and, on a rank-up, everyone. What was added.</summary>
    int Grant(ServerClient c, int amount)
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

    /// <summary>Tells each running Job's party when it changed (a member came or went, a new leader); one who went is told "no job" unless another Job has them now.</summary>
    void TellParties()
    {
        foreach (var job in _book.Running)
        {
            var members = _parties.MembersOf(job.Owner).ToArray();
            if (_told.TryGetValue(job.Id, out var was) && was.Leader == job.Leader && was.Members.AsSpan().SequenceEqual(members)) continue;
            _told[job.Id] = (job.Leader, members);
            _objectiveSent.Remove(job.Id); // the objective goes again to everyone, a member who joined midway among them
            foreach (var m in members)
                if (_ctx.Players.TryGetValue(m, out var mc) && !mc.IsBot) _ctx.SendEvent(mc, EventKind.CartelJob, CartelCodec.EncodeJob(JobMsg(job, mc, 0)));
            if (was.Members == null) continue;
            foreach (var gone in was.Members)
                if (Array.IndexOf(members, gone) < 0 && _book.Involving(gone, _parties) == null && _ctx.Players.TryGetValue(gone, out var gc) && !gc.IsBot)
                    _ctx.SendEvent(gc, EventKind.CartelJob, CartelCodec.EncodeJob(CartelJobMsg.None));
        }
    }

    /// <summary>
    /// Sends each running Job's party its objective line and marker (<see cref="IJobObjective.Objective"/>, <see cref="IJobObjective.Marker"/>) when the line changed, the marker
    /// came or went or moved more than <see cref="CartelFile.MarkerResendM"/>, or the party changed (<see cref="TellParties"/> forgets what was sent).
    /// </summary>
    void TellObjectives()
    {
        foreach (var job in _book.Running)
        {
            var line = job.Objective.Objective(job) ?? "";
            var marker = job.Objective.Marker(job);
            if (_objectiveSent.TryGetValue(job.Id, out var was) && was.Line == line && SameMarker(was.Marker, marker)) continue;
            _objectiveSent[job.Id] = (line, marker);
            var payload = CartelCodec.EncodeObjective(new CartelObjectiveMsg(job.Id, line, marker));
            foreach (var m in _parties.MembersOf(job.Owner))
                if (_ctx.Players.TryGetValue(m, out var mc) && !mc.IsBot) _ctx.SendEvent(mc, EventKind.CartelObjective, payload);
        }
    }

    bool SameMarker(System.Numerics.Vector3? a, System.Numerics.Vector3? b) =>
        a is { } x ? b is { } y && System.Numerics.Vector3.Distance(x, y) <= _data.MarkerResendM : b == null;

    static CartelJobMsg JobMsg(Job job, ServerClient to, int paid)
    {
        var flags = CartelJobFlags.None;
        if (Earns(to)) flags |= CartelJobFlags.Earns;
        if (to.Profile?.Cartel.IsMember != true) flags |= CartelJobFlags.NotMember;
        if (job.Leader == to.Id) flags |= CartelJobFlags.Leader;
        return new CartelJobMsg(job.Id, job.State, job.Reason, job.Kind.Id, (byte)job.Tier, job.Leader, Math.Max(0, job.SecondsLeft), paid, flags);
    }

    void SendStatus(ServerClient c, bool rankedUp = false)
    {
        if (c.IsBot || c.Profile is not { } p) return;
        var flags = CartelStatusFlags.None;
        if (!p.Persist) flags |= CartelStatusFlags.Guest;
        if (c.Sandbox) flags |= CartelStatusFlags.Sandbox;
        if (rankedUp) flags |= CartelStatusFlags.RankedUp;
        _ctx.SendEvent(c, EventKind.CartelStatus, CartelCodec.EncodeStatus(new CartelStatusMsg(p.Cartel.Membership, p.Cartel.Respect, (byte)p.Cartel.Rank(_data), flags)));
    }

    string RankName(CartelStanding s) => _data.Ranks[s.Rank(_data)].Name;

    /// <summary>
    /// "/cartel join | quit | respect &lt;n&gt; | job &lt;kind&gt; [tier] | status" (admin, for testing, on themselves): join or quit anywhere, add Respect (a rank-up is announced as
    /// after a Job), start a Job of any tier anywhere (the other checks stand), or say the standing and the Jobs that run.
    /// </summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        var rest = args.Count > 1 ? args[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        switch (args[0])
        {
            case "join": Join(c, admin: true); break;
            case "quit": Quit(c, admin: true); break;
            case "respect":
                if (rest.Length != 1 || !int.TryParse(rest[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n > MaxGrant)
                { _ctx.ChatTo(c, ChatLineKind.Error, $"Say how much: /cartel respect <0 to {MaxGrant}>."); break; }
                if (c.Profile is not { Cartel.IsMember: true } p) { _ctx.ChatTo(c, ChatLineKind.Error, "Join the Cartel first: /cartel join."); break; }
                var added = Grant(c, n);
                _ctx.ChatTo(c, ChatLineKind.Info, $"+{added} Respect: {p.Cartel.Respect} now, {RankName(p.Cartel)}.");
                break;
            case "job":
                var tier = 1;
                if (rest.Length is < 1 or > 2 || (rest.Length == 2 && !int.TryParse(rest[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out tier)))
                { _ctx.ChatTo(c, ChatLineKind.Error, $"Say the kind and the tier: /cartel job <{string.Join("|", _book.Kinds.Select(k => k.Id))}> [1-{_data.Tiers.Count}]."); break; }
                StartJob(c, rest[0].ToLowerInvariant(), tier, admin: true);
                break;
            case "status":
                var s = c.Profile?.Cartel ?? new CartelStanding();
                _ctx.ChatTo(c, ChatLineKind.Info, $"Cartel: {s.Membership}, {s.Respect} Respect, {RankName(s)}, tiers up to {_data.HighestTier(s.Rank(_data))}. {_book.Running.Count} job(s) running.");
                if (_book.Involving(c.Id, _parties) is { } job) _ctx.ChatTo(c, ChatLineKind.Info, $"Your job: {job.Kind.Name} tier {job.Tier}, led by {_ctx.NameOf(job.Leader)}, {Math.Max(0, job.SecondsLeft):F0} s left.");
                break;
        }
    }
}
