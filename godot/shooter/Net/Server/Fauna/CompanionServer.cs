using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Wildlife;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Fauna;

/// <summary>
/// Companions on the server (GLOSSARY "Companion", #488): one animal per player that stays with them (wire type 58), kept by a <see cref="CompanionKeeper"/>. The server only
/// says who has which Companion out and where each owner is; what a Companion does is its kind's (ShooterCore, <see cref="Companion"/>). A Companion is in the world while its
/// owner is alive and on foot: it goes when they die or take a seat and appears beside them again after. A player has the first Companion their Profile owns (an unlock row
/// of kind <see cref="CompanionRules.UnlockKind"/>: a collection tier's reward) out from the join on, or the one they chose among those they own (the Equipment screen's picker, <see cref="CompanionRoster"/>; the choice is stored
/// with the Profile); <c>/companion</c> gives one for the session.
/// </summary>
internal sealed class CompanionServer
{
    readonly ServerContext _ctx;
    readonly CompanionsFile _data;
    readonly IProgressionStore? _store;
    readonly bool _unlockAll;
    readonly ICompanionWorld _world;
    readonly CompanionKeeper _keeper;

    /// <summary>The marks each owner was told last (absent: none).</summary>
    readonly Dictionary<ushort, ushort[]> _marksSent = new();

    /// <summary>The Companion <c>/companion</c> gave a player for this session: they can pick it, though their Profile does not own it.</summary>
    readonly Dictionary<ushort, string> _given = new();

    /// <summary>Registers the Companion type and starts the keeper. Built once from <c>_Ready</c>, after the match exists.</summary>
    public CompanionServer(ServerContext ctx, CompanionsFile data, AnimalsFile animals, ICompanionWorld world, IProgressionStore? store, bool unlockAll)
    {
        _ctx = ctx; _data = data; _store = store; _unlockAll = unlockAll; _world = world;
        CompanionRules.Register(WorldEntityRegistry.Shared);
        _keeper = new CompanionKeeper(data, animals, ctx.Match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
    }

    /// <summary>Once a tick, after the entities: each Companion is told where its owner is; an owner who is dead, in a seat or gone has none in the world.</summary>
    public void Tick()
    {
        _keeper.Update(_world, OwnerOf);
        SendMarks();
    }

    /// <summary>Tells each owner whom their Companion has marked, when that changed: a new set, or none (the marks ran out, or the Companion went with its owner's death).</summary>
    void SendMarks()
    {
        foreach (var c in _ctx.Players.Values)
        {
            if (c.IsBot) continue;
            var now = _keeper.Of(c.Id)?.Marks ?? Array.Empty<ushort>();
            var had = _marksSent.GetValueOrDefault(c.Id) ?? Array.Empty<ushort>();
            if (now.Count == had.Length && now.SequenceEqual(had)) continue;
            if (now.Count == 0) _marksSent.Remove(c.Id); else _marksSent[c.Id] = now.ToArray();
            _ctx.SendEvent(c, EventKind.CompanionMarks, ScoutCompanion.EncodeMarks(now));
        }
    }

    CompanionOwner? OwnerOf(ushort player)
    {
        if (!_ctx.Players.TryGetValue(player, out var c) || c.Car != null || !_ctx.IsAlive(player)) return null;
        return new CompanionOwner(c.Body.GlobalPosition.ToNumerics(), c.Body.Yaw);
    }

    /// <summary>A round starts: the match cleared the entities; the players keep the Companion they had out and it appears again when they are alive.</summary>
    public void RoundStart() => _keeper.Clear();

    /// <summary>The player left: their Companion goes with them.</summary>
    public void Leave(ushort id)
    {
        _keeper.Forget(id);
        _given.Remove(id);
        _marksSent.Remove(id);
    }

    /// <summary>What the player can pick and what is out (<see cref="CompanionRoster"/>): the Profile's unlock rows, the one given for the session, all of them in dev mode.</summary>
    CompanionRoster RosterOf(ServerClient c) => CompanionRoster.Of(_data, c.Profile?.Unlocks ?? (IEnumerable<Unlock>)Array.Empty<Unlock>(), _unlockAll,
        _given.GetValueOrDefault(c.Id), _keeper.Assigned(c.Id));

    /// <summary>Tells the player their roster: at join and whenever what they own or what is out changes.</summary>
    public void SendRoster(ServerClient c)
    {
        if (c.IsBot) return;
        _ctx.SendEvent(c, EventKind.Companions, RosterOf(c).Encode());
    }

    /// <summary>The Companion the Profile chose is out, or the first it owns (a player who owns none has none; one given by <c>/companion</c> stays).</summary>
    public void FromProfile(ServerClient c)
    {
        if (c.Profile is not { } p || _keeper.Assigned(c.Id) != null) return;
        var def = RosterOf(c).Default(_data, p.Companion);
        if (def >= 0) _keeper.Assign(c.Id, _data.Companions[def].Id);
    }

    /// <summary>
    /// The player picked a Companion on the Equipment screen: it is out from now on (the old one goes in a puff, the new one appears beside them) and the choice is stored
    /// with the Profile. One they cannot pick is not answered (the screen never offers it).
    /// </summary>
    public void OnPick(ServerClient c, byte[] payload)
    {
        if (c.Profile is not { } p || !CompanionRoster.TryDecodePick(payload, out var def)) return;
        if (!RosterOf(c).Owns(def)) return;
        var id = _data.Companions[def].Id;
        _keeper.Assign(c.Id, id);
        if (p.Companion != id)
        {
            p.Companion = id;
            if (p.Persist) _ = _store!.Profiles.SetCompanionAsync(p.DbId, id);
        }
        SendRoster(c);
    }

    /// <summary>"/companion id|off [player]": the player (the admin without a name) has that Companion out for this session, or none. Nothing is stored.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        var target = args.Count > 1 ? _ctx.FindHuman(args[1]) : c;
        if (target == null) { _ctx.ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        var off = args[0] == ChatCommands.CompanionOff;
        if (!_keeper.Assign(target.Id, off ? null : args[0])) { _ctx.ChatTo(c, ChatLineKind.Error, $"Unknown companion '{args[0]}'."); return; }
        if (!off) _given[target.Id] = args[0]; // theirs to pick again for the session
        SendRoster(target);
        var who = target == c ? "You have" : $"{target.Name} has";
        _ctx.ChatTo(c, ChatLineKind.Info, off ? $"{who} no Companion now." : $"{who} the {_data.Find(args[0])!.Name} as a Companion now. It appears beside a living player on foot.");
    }
}
