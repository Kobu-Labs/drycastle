using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Cartel on the server (docs/cartel.md, ADR 0045, #494): the Contact's requests, the Jobs and what they pay. A player asks at the Contact (<see cref="EventKind.CartelRequest"/>:
/// join, quit, start a Job); the rules are ShooterCore's (<see cref="CartelRules"/>, <see cref="JobBook"/>); a refusal is a chat line and, for a request from the panel, the
/// refusal event (<see cref="EventKind.CartelRefused"/>) the panel shows. Membership, Respect and the standing are <see cref="CartelMembers"/>'; who is told what about the running
/// Jobs is <see cref="JobFeed"/>'s (<see cref="EventKind.CartelJob"/>, <see cref="EventKind.CartelObjective"/>), sent here. Who runs a Job is <see cref="IJobParties"/>'s (ffa: solo;
/// tdm: the Squad and its leader). Kinds of Job plug in with <see cref="Register"/>; the test kind is built in, for admins only. No Job outlives its round: the end screen fails them.
/// </summary>
internal sealed class CartelServer
{
    /// <summary>Most Respect one <c>/cartel respect</c> adds.</summary>
    const int MaxGrant = 10_000_000;

    readonly ServerContext _ctx;
    readonly CartelFile _data;
    readonly IJobParties _parties;
    readonly IContactSite _contact;
    readonly Func<ServerClient, bool> _isAdmin;
    readonly Func<WorldQuery> _query;
    readonly CartelMembers _members;
    readonly JobBook _book = new();
    readonly List<JobEnded> _ended = new();
    readonly JobFeed _feed;
    readonly List<JobFeedMessage> _outbox = new();
    /// <summary>The humans the feed tells (a bot reads nothing), filled each tick.</summary>
    readonly List<ushort> _receivers = new();
    readonly Func<Job, ushort, byte> _flagsOf;

    /// <param name="parties">Who runs a Job (<see cref="MatchJobParties"/>).</param>
    /// <param name="contact">Where the Contact stands; <see cref="MapContactSite"/>, the map's placed Contact.</param>
    /// <param name="isAdmin">The chat's admin check: only an admin may start the test kind (it pays for nothing done).</param>
    /// <param name="query">The world query: the ground under a Job's marker, where the admin's <c>/cartel goto</c> lands.</param>
    public CartelServer(ServerContext ctx, CartelFile data, IProgressionStore? store, IJobParties parties, IContactSite contact, Func<ServerClient, bool> isAdmin, Func<WorldQuery> query)
    {
        _ctx = ctx; _data = data; _parties = parties; _contact = contact; _isAdmin = isAdmin; _query = query;
        _members = new CartelMembers(ctx, data, store);
        _feed = new JobFeed(data.MarkerResendM);
        _flagsOf = FlagsOf;
        Register(new TestJobKind(data));
    }

    /// <summary>The running Jobs and their reservations (a kind's server part claims its drop points here).</summary>
    public JobBook Jobs => _book;

    /// <summary>Adds a kind of Job (Delivery, the heists, Hit: their own packages).</summary>
    public void Register(IJobKind kind) => _book.Register(kind);

    /// <summary>Once a tick: the Jobs step (objectives, timers, leaders); what ended is paid; the parties are told what changed. Out of a running round every Job fails.</summary>
    public void Tick(float dt)
    {
        if (_book.Running.Count == 0) return;
        if (_ctx.Match.Phase != MatchPhase.Running)
        {
            // the end screen (or waiting for players): the round is over, so are its Jobs, at once rather than at the next round's reset
            _book.FailAll(JobFailReason.RoundReset, _parties, _ended);
            Settle();
            return;
        }
        _book.Tick(dt, _parties, _ended);
        Settle();
        Tell();
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
    public void SendOnJoin(ServerClient c) => _members.SendStatus(c);

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
    bool ContactAnswers(ServerClient c, bool admin, CartelAction action)
    {
        var start = action == CartelAction.StartJob;
        if (!_contact.Present)
        {
            if (start) Refuse(c, action, (byte)JobStartRefusal.NoCartel, CartelWords.Start.Text(JobStartRefusal.NoCartel), admin);
            else Refuse(c, action, (byte)CartelJoinRefusal.NoCartel, CartelWords.Join.Text(CartelJoinRefusal.NoCartel), admin);
            return false;
        }
        if (admin || AtContact(c)) return true;
        if (start) Refuse(c, action, (byte)JobStartRefusal.FarFromContact, CartelWords.Start.Text(JobStartRefusal.FarFromContact), admin);
        else Refuse(c, action, (byte)CartelJoinRefusal.FarFromContact, CartelWords.Join.Text(CartelJoinRefusal.FarFromContact), admin);
        return false;
    }

    /// <summary>A refused request: a chat line, and for the panel's request the refusal event its panel shows (the admin's command has only the chat).</summary>
    void Refuse(ServerClient c, CartelAction action, byte code, string words, bool admin)
    {
        _ctx.ChatTo(c, ChatLineKind.Error, words);
        if (!admin) _ctx.SendEvent(c, EventKind.CartelRefused, CartelCodec.EncodeRefusal(new CartelRefusalMsg(action, code, words)));
    }

    void Join(ServerClient c, bool admin)
    {
        if (!ContactAnswers(c, admin, CartelAction.Join)) return;
        var why = _members.Join(c);
        if (why != CartelJoinRefusal.None) Refuse(c, CartelAction.Join, (byte)why, CartelRules.Words(why), admin);
    }

    void Quit(ServerClient c, bool admin)
    {
        if (!ContactAnswers(c, admin, CartelAction.Quit)) return;
        var why = _members.Quit(c);
        if (why != CartelJoinRefusal.None) { Refuse(c, CartelAction.Quit, (byte)why, CartelRules.Words(why), admin); return; }
        _book.LeaderQuit(c.Id, _parties, _ended); // the Job they lead fails
        Settle();
    }

    void StartJob(ServerClient c, string kindId, int tier, bool admin)
    {
        if (!ContactAnswers(c, admin, CartelAction.StartJob)) return;
        var kind = _book.Kind(kindId);
        if (kind?.Id == TestJobKind.KindId && !_isAdmin(c)) kind = null; // it pays a tier's Respect for 20 s of nothing: admins only, the panel's request too
        var owner = _parties.OwnerOf(c.Id);
        var members = _parties.MembersOf(owner);
        var standing = c.Profile?.Cartel;
        var facts = new JobStartFacts(c.IsBot, standing?.IsMember == true, _parties.LeaderOf(owner) == c.Id, kind != null, tier, standing?.Rank(_data) ?? 0,
            _ctx.Match.Phase == MatchPhase.Running, _ctx.IsAlive(c.Id), AtContact(c), _book.OfOwner(owner) != null, members.Any(m => _book.Involving(m, _parties) != null), admin);
        var why = CartelRules.CanStart(_data, facts);
        if (why != JobStartRefusal.None) { Refuse(c, CartelAction.StartJob, (byte)why, CartelRules.Words(why, _data, tier), admin); return; }
        if (_book.Start(CartelRules.Giver, kind!, tier, owner, c.Id, _parties, out var refusal) is not { } job)
        {
            Refuse(c, CartelAction.StartJob, (byte)JobStartRefusal.CannotBegin, refusal ?? CartelWords.Start.Text(JobStartRefusal.CannotBegin), admin);
            return;
        }
        Tell();
        foreach (var m in members)
        {
            if (!_ctx.Players.TryGetValue(m, out var mc) || mc.IsBot) continue;
            _ctx.ChatTo(mc, ChatLineKind.Info, $"{c.Name} started the job {kind!.Name} (tier {tier}): {job.Seconds:F0} s.");
            if (CartelMembers.FlagsOf(mc).HasFlag(CartelJobFlags.NotMember)) _ctx.ChatTo(mc, ChatLineKind.Info, CartelWords.NoRespect.Text(NoRespectReason.NotMember)); // a guest cannot join: not told to
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
            _feed.Forget(job.Id);
            var reward = e.Succeeded ? CartelRules.Reward(_data, job) : 0;
            foreach (var m in e.Members)
            {
                if (!_ctx.Players.TryGetValue(m, out var mc) || mc.IsBot) continue;
                var paid = 0;
                if (e.Succeeded && CartelMembers.Earns(mc)) paid = _members.Grant(mc, reward);
                _ctx.SendEvent(mc, EventKind.CartelJob, CartelCodec.EncodeJob(JobMsg(job, (CartelJobFlags)FlagsOf(job, m), paid)));
                _ctx.ChatTo(mc, ChatLineKind.Info, !e.Succeeded ? $"Job failed. {CartelWords.Fail.Text(job.Reason)}."
                    : paid > 0 ? $"Job done: +{paid} Respect." : $"Job done. {CartelWords.NoRespect.Text(CartelMembers.NoRespect(mc))}");
            }
            GD.Print($"server: {job} ended{(e.Succeeded ? $", {reward} Respect each to {e.Members.Count(m => _ctx.Players.TryGetValue(m, out var o) && CartelMembers.Earns(o))} member(s)" : $": {job.Reason}")}");
        }
        _ended.Clear();
    }

    /// <summary>Sends what the feed says changed: a Job to a member (joined, new leader, new flags), "no job" to a leaver, the objective to the party.</summary>
    void Tell()
    {
        _receivers.Clear();
        foreach (var c in _ctx.Roster) if (!c.IsBot) _receivers.Add(c.Id);
        _outbox.Clear();
        _feed.Tick(_book.Running, _parties, _receivers, _flagsOf, _outbox);
        foreach (var m in _outbox)
        {
            if (!_ctx.Players.TryGetValue(m.To, out var to)) continue;
            switch (m.Kind)
            {
                case JobFeedKind.Job: _ctx.SendEvent(to, EventKind.CartelJob, CartelCodec.EncodeJob(JobMsg(m.Job!, (CartelJobFlags)m.Flags, 0))); break;
                case JobFeedKind.NoJob: _ctx.SendEvent(to, EventKind.CartelJob, CartelCodec.EncodeJob(CartelJobMsg.None)); break;
                case JobFeedKind.Objective: _ctx.SendEvent(to, EventKind.CartelObjective, CartelCodec.EncodeObjective(new CartelObjectiveMsg(m.Job!.Id, m.Line, m.Marker))); break;
            }
        }
    }

    /// <summary>The receiver's flags for a Job: theirs (<see cref="CartelMembers.FlagsOf"/>) and whether they lead it.</summary>
    byte FlagsOf(Job job, ushort player)
    {
        var flags = _ctx.Players.TryGetValue(player, out var c) ? CartelMembers.FlagsOf(c) : CartelJobFlags.None;
        if (job.Leader == player) flags |= CartelJobFlags.Leader;
        return (byte)flags;
    }

    static CartelJobMsg JobMsg(Job job, CartelJobFlags flags, int paid) =>
        new(job.Id, job.State, job.Reason, job.Kind.Id, (byte)job.Tier, job.Leader, Math.Max(0, job.SecondsLeft), paid, flags);

    /// <summary>
    /// "/cartel join | quit | respect &lt;n&gt; | job &lt;kind&gt; [tier] | status" (admin, for testing, on themselves): join or quit anywhere, add Respect (a rank-up is announced as
    /// after a Job), start a Job of any tier anywhere (the other checks stand), or say the standing and the Jobs that run. On the admin's own running Job (the one involving them), to reach
    /// its late stages: "end win | fail" ends it now (a win pays as a success, a fail reads "Ended by an admin"), "time &lt;seconds&gt;" sets its timer, "assault" sends an Assault on a heist
    /// (the clock's path and cap), "goto" puts the admin on the ground under its marker.
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
                var added = _members.Grant(c, n);
                _ctx.ChatTo(c, ChatLineKind.Info, $"+{added} Respect: {p.Cartel.Respect} now, {_members.RankName(p.Cartel)}.");
                break;
            case "job":
                var tier = 1;
                if (rest.Length is < 1 or > 2 || (rest.Length == 2 && !int.TryParse(rest[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out tier)))
                { _ctx.ChatTo(c, ChatLineKind.Error, $"Say the kind and the tier: /cartel job <{string.Join("|", _book.Kinds.Select(k => k.Id))}> [1-{_data.Tiers.Count}]."); break; }
                StartJob(c, rest[0].ToLowerInvariant(), tier, admin: true);
                break;
            case "status":
                var s = c.Profile?.Cartel ?? new CartelStanding();
                _ctx.ChatTo(c, ChatLineKind.Info, $"Cartel: {s.Membership}, {s.Respect} Respect, {_members.RankName(s)}, tiers up to {_data.HighestTier(s.Rank(_data))}. {_book.Running.Count} job(s) running.");
                if (_book.Involving(c.Id, _parties) is { } job) _ctx.ChatTo(c, ChatLineKind.Info, $"Your job: {job.Kind.Name} tier {job.Tier}, led by {_ctx.NameOf(job.Leader)}, {Math.Max(0, job.SecondsLeft):F0} s left.");
                break;
            case "end": AdminEnd(c, rest); break;
            case "time": AdminTime(c, rest); break;
            case "assault": AdminAssault(c); break;
            case "goto": AdminGoto(c); break;
        }
    }

    /// <summary>The admin's own running Job, or null (they are told to start one).</summary>
    Job? AdminJob(ServerClient c)
    {
        if (_book.Involving(c.Id, _parties) is { } job) return job;
        _ctx.ChatTo(c, ChatLineKind.Error, "You run no job: start one with /cartel job <kind> [tier].");
        return null;
    }

    void AdminEnd(ServerClient c, string[] rest)
    {
        if (rest.Length != 1 || CartelAdmin.ParseEnd(rest[0]) is not { } won) { _ctx.ChatTo(c, ChatLineKind.Error, "Say how it ends: /cartel end win|fail."); return; }
        if (AdminJob(c) is not { } job) return;
        GD.Print($"server: {c.Name} #{c.Id} ended {job} as a {(won ? "win" : "fail")} (admin)");
        _book.EndByAdmin(job, won, _parties, _ended);
        Settle(); // the party hears the end and, on a win, is paid
    }

    void AdminTime(ServerClient c, string[] rest)
    {
        if (rest.Length != 1 || CartelAdmin.ParseSeconds(rest[0]) is not { } seconds) { _ctx.ChatTo(c, ChatLineKind.Error, $"Say the seconds left: /cartel time <1 to {CartelAdmin.MaxTimerSeconds}>."); return; }
        if (AdminJob(c) is not { } job) return;
        job.SetSecondsLeft(seconds);
        foreach (var m in job.Party) // the tracker counts down from the last Job message: send it again
            if (_ctx.Players.TryGetValue(m, out var mc) && !mc.IsBot) _ctx.SendEvent(mc, EventKind.CartelJob, CartelCodec.EncodeJob(JobMsg(job, (CartelJobFlags)FlagsOf(job, m), 0)));
        _ctx.ChatTo(c, ChatLineKind.Info, $"Your job has {seconds} s left.");
    }

    void AdminAssault(ServerClient c)
    {
        if (AdminJob(c) is not { } job) return;
        if (job.Objective is not HeistRun run) { _ctx.ChatTo(c, ChatLineKind.Error, $"Your job ({job.Kind.Name}) is no heist: only a heist has Assaults."); return; }
        if (run.SendAssault() is { } why) _ctx.ChatTo(c, ChatLineKind.Error, why);
    }

    void AdminGoto(ServerClient c)
    {
        if (AdminJob(c) is not { } job) return;
        if (job.Objective.Marker(job) is not { } marker) { _ctx.ChatTo(c, ChatLineKind.Error, "Your job has no marker now."); return; }
        if (!_ctx.IsAlive(c.Id)) { _ctx.ChatTo(c, ChatLineKind.Error, "Only a living player can go there."); return; }
        if (c.Car != null) { _ctx.ChatTo(c, ChatLineKind.Error, "Get out of the vehicle first."); return; }
        var at = new Vector3(marker.X, marker.Y, marker.Z);
        // the first surface under the marker (a floor indoors, the ground outdoors), looked for from a little above it so a marker at floor height still finds that floor
        var feet = _query().Ground(MoverKind.Player, at, GotoUpM, GotoDownM) is { } ground ? ground.Position : at;
        c.Body.Teleport(new Vector3(feet.X, feet.Y + 0.1f, feet.Z));
        c.Inputs.Clear();
        var s = new System.Numerics.Vector3(feet.X, feet.Y, feet.Z);
        _ctx.Match.SetPosition(c.Id, s);
        _ctx.Broadcast(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(c.Id, s, 0))); // as the sandbox's teleport: the client takes the new place at once
        _ctx.ChatTo(c, ChatLineKind.Info, $"At your job's marker ({feet.X:F0}, {feet.Z:F0}).");
    }

    /// <summary>/cartel goto looks for the surface under the marker from this far above it down to this far below.</summary>
    const float GotoUpM = 1.5f, GotoDownM = 200f;
}
