using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Companions on the server (GLOSSARY "Companion", #488): one animal per player that stays with them (wire type 58), kept by a <see cref="CompanionKeeper"/>. The server only
/// says who has which Companion out and where each owner is; what a Companion does is its kind's (ShooterCore, <see cref="Companion"/>). A Companion is in the world while its
/// owner is alive and on foot: it goes when they die or take a seat and appears beside them again after. A player has the first Companion their Profile owns (an unlock row
/// of kind <see cref="CompanionRules.UnlockKind"/>: a collection tier's reward) out from the join on; <c>/companion</c> gives one for the session.
/// </summary>
public partial class NetServer
{
    /// <summary>companions.json: the Companions of the game (set by the host).</summary>
    public CompanionsFile CompanionData = new();

    CompanionKeeper? _companions;
    CompanionGround? _companionGround;

    /// <summary>The ground a Companion walks on: the terrain, as shallow as an animal wades. It fears nothing.</summary>
    sealed class CompanionGround : AnimalGroundBase
    {
        public CompanionGround(NetServer server) : base(server) { }

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
    }

    CompanionOwner? CompanionOwnerOf(ushort player)
    {
        if (!_byId.TryGetValue(player, out var c) || c.Car != null || _match.Get(player)?.Life != PlayerLife.Alive) return null;
        return new CompanionOwner(N(c.Body.GlobalPosition), c.Body.Yaw);
    }

    /// <summary>A round starts: the match cleared the entities; the players keep the Companion they had out and it appears again when they are alive.</summary>
    void CompanionsRoundStart() => _companions?.Clear();

    /// <summary>The player left: their Companion goes with them.</summary>
    void CompanionLeave(ushort id) => _companions?.Forget(id);

    /// <summary>The first Companion the Profile owns is out (a player who owns none has none; one given by <c>/companion</c> stays).</summary>
    void CompanionFromProfile(Client c)
    {
        if (_companions == null || c.Profile is not { } p || _companions.Assigned(c.Id) != null) return;
        foreach (var u in p.Unlocks)
            if (u.Kind == CompanionRules.UnlockKind && _companions.Assign(c.Id, u.ItemId)) return;
    }

    /// <summary>"/companion id|off [player]": the player (the admin without a name) has that Companion out for this session, or none. Nothing is stored.</summary>
    void ChatCompanion(Client c, IReadOnlyList<string> args)
    {
        if (_companions == null) return;
        var target = args.Count > 1 ? FindHuman(args[1]) : c;
        if (target == null) { ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        var off = args[0] == ChatCommands.CompanionOff;
        if (!_companions.Assign(target.Id, off ? null : args[0])) { ChatTo(c, ChatLineKind.Error, $"Unknown companion '{args[0]}'."); return; }
        var who = target == c ? "You have" : $"{target.Name} has";
        ChatTo(c, ChatLineKind.Info, off ? $"{who} no Companion now." : $"{who} the {CompanionData.Find(args[0])!.Name} as a Companion now. It appears beside a living player on foot.");
    }
}
