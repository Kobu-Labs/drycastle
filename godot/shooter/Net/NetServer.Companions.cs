using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Companions on the server (GLOSSARY "Companion", #488): one animal per player that stays with them (wire type 58), kept by a <see cref="CompanionKeeper"/>. The server only
/// says who has which Companion out and where each owner is; what a Companion does is its kind's (ShooterCore, <see cref="Companion"/>). A Companion is in the world while its
/// owner is alive and on foot: it goes when they die or take a seat and appears beside them again after. A player has the first Companion their Profile owns (an unlock row
/// of kind <see cref="CompanionRules.UnlockKind"/>: a collection tier's reward) out from the join on, or the one they chose among those they own (the Equipment screen's picker, <see cref="CompanionRoster"/>; the choice is stored
/// with the Profile); <c>/companion</c> gives one for the session.
/// </summary>
public partial class NetServer
{
    /// <summary>companions.json: the Companions of the game (set by the host).</summary>
    public CompanionsFile CompanionData = new();

    CompanionKeeper? _companions;
    CompanionGround? _companionGround;

    /// <summary>The ground a Companion walks on: the terrain, as shallow as an animal wades. It fears nothing. It can say who its owner's enemies are (a scout marks them).</summary>
    sealed class CompanionGround : AnimalGroundBase, ICompanionWorld
    {
        public CompanionGround(NetServer server) : base(server) { }

        /// <summary>Every living player who is not the owner and not on their team (everyone else in a free-for-all), bots too, on foot or in a seat.</summary>
        public void EnemiesNear(ushort owner, NVec3 at, float radius, List<CompanionSighting> into)
        {
            foreach (var c in _s._byId.Values)
            {
                if (c.Id == owner || _s._match.AreFriends(owner, c.Id) || _s._match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
                var p = c.Body.GlobalPosition;
                var dx = p.X - at.X; var dz = p.Z - at.Z;
                if (dx * dx + dz * dz <= radius * radius) into.Add(new CompanionSighting(c.Id, N(p)));
            }
        }

        protected override float WadeM => PetWadeM;

        public override NVec3? NearestThreat(NVec3 from, float radius) => null;
    }

    /// <summary>Registers the Companion type and starts the keeper. Called once from <c>_Ready</c>, after the match exists.</summary>
    void InitCompanions()
    {
        CompanionRules.Register(WorldEntityRegistry.Shared);
        _companions = new CompanionKeeper(CompanionData, AnimalData, _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
    }

    /// <summary>Once a tick, after the entities: each Companion is told where its owner is; an owner who is dead, in a seat or gone has none in the world.</summary>
    void CompanionsTick()
    {
        if (_companions == null) return;
        _companions.Update(_companionGround ??= new CompanionGround(this), CompanionOwnerOf);
        SendCompanionMarks();
    }

    /// <summary>The marks each owner was told last (absent: none).</summary>
    readonly Dictionary<ushort, ushort[]> _companionMarksSent = new();

    /// <summary>Tells each owner whom their Companion has marked, when that changed: a new set, or none (the marks ran out, or the Companion went with its owner's death).</summary>
    void SendCompanionMarks()
    {
        foreach (var c in _byId.Values)
        {
            if (c.IsBot) continue;
            var now = _companions!.Of(c.Id)?.Marks ?? Array.Empty<ushort>();
            var had = _companionMarksSent.GetValueOrDefault(c.Id) ?? Array.Empty<ushort>();
            if (now.Count == had.Length && now.SequenceEqual(had)) continue;
            if (now.Count == 0) _companionMarksSent.Remove(c.Id); else _companionMarksSent[c.Id] = now.ToArray();
            Send(c, new EventMsg((byte)EventKind.CompanionMarks, ScoutCompanion.EncodeMarks(now)));
        }
    }

    CompanionOwner? CompanionOwnerOf(ushort player)
    {
        if (!_byId.TryGetValue(player, out var c) || c.Car != null || _match.Get(player)?.Life != PlayerLife.Alive) return null;
        return new CompanionOwner(N(c.Body.GlobalPosition), c.Body.Yaw);
    }

    /// <summary>A round starts: the match cleared the entities; the players keep the Companion they had out and it appears again when they are alive.</summary>
    void CompanionsRoundStart() => _companions?.Clear();

    /// <summary>The player left: their Companion goes with them.</summary>
    void CompanionLeave(ushort id)
    {
        _companions?.Forget(id);
        _companionGiven.Remove(id);
        _companionMarksSent.Remove(id);
    }

    /// <summary>The Companion <c>/companion</c> gave a player for this session: they can pick it, though their Profile does not own it.</summary>
    readonly Dictionary<ushort, string> _companionGiven = new();

    /// <summary>What the player can pick and what is out (<see cref="CompanionRoster"/>): the Profile's unlock rows, the one given for the session, all of them in dev mode.</summary>
    CompanionRoster CompanionRosterOf(Client c) => CompanionRoster.Of(CompanionData, c.Profile?.Unlocks ?? (IEnumerable<Unlock>)Array.Empty<Unlock>(), UnlockAll,
        _companionGiven.GetValueOrDefault(c.Id), _companions?.Assigned(c.Id));

    /// <summary>Tells the player their roster: at join and whenever what they own or what is out changes.</summary>
    void SendCompanions(Client c)
    {
        if (_companions == null || c.IsBot) return;
        Send(c, new EventMsg((byte)EventKind.Companions, CompanionRosterOf(c).Encode()));
    }

    /// <summary>The Companion the Profile chose is out, or the first it owns (a player who owns none has none; one given by <c>/companion</c> stays).</summary>
    void CompanionFromProfile(Client c)
    {
        if (_companions == null || c.Profile is not { } p || _companions.Assigned(c.Id) != null) return;
        var def = CompanionRosterOf(c).Default(CompanionData, p.Companion);
        if (def >= 0) _companions.Assign(c.Id, CompanionData.Companions[def].Id);
    }

    /// <summary>
    /// The player picked a Companion on the Equipment screen: it is out from now on (the old one goes in a puff, the new one appears beside them) and the choice is stored
    /// with the Profile. One they cannot pick is not answered (the screen never offers it).
    /// </summary>
    void OnCompanionPick(Client c, byte[] payload)
    {
        if (_companions == null || c.Profile is not { } p || !CompanionRoster.TryDecodePick(payload, out var def)) return;
        if (!CompanionRosterOf(c).Owns(def)) return;
        var id = CompanionData.Companions[def].Id;
        _companions.Assign(c.Id, id);
        if (p.Companion != id)
        {
            p.Companion = id;
            if (p.Persist) _ = Progression!.Profiles.SetCompanionAsync(p.DbId, id);
        }
        SendCompanions(c);
    }

    /// <summary>"/companion id|off [player]": the player (the admin without a name) has that Companion out for this session, or none. Nothing is stored.</summary>
    void ChatCompanion(Client c, IReadOnlyList<string> args)
    {
        if (_companions == null) return;
        var target = args.Count > 1 ? FindHuman(args[1]) : c;
        if (target == null) { ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        var off = args[0] == ChatCommands.CompanionOff;
        if (!_companions.Assign(target.Id, off ? null : args[0])) { ChatTo(c, ChatLineKind.Error, $"Unknown companion '{args[0]}'."); return; }
        if (!off) _companionGiven[target.Id] = args[0]; // theirs to pick again for the session
        SendCompanions(target);
        var who = target == c ? "You have" : $"{target.Name} has";
        ChatTo(c, ChatLineKind.Info, off ? $"{who} no Companion now." : $"{who} the {CompanionData.Find(args[0])!.Name} as a Companion now. It appears beside a living player on foot.");
    }
}
