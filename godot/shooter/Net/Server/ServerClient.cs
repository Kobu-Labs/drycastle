using Godot;
using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter;

/// <summary>One connection of the server: a human at a peer, or a bot (no peer). Plain state; the rules that read and write it live in the feature that owns the field.</summary>
internal sealed class ServerClient
{
    /// <summary>The network peer; null for a bot (#214), which has no connection: nothing is sent to it and its inputs come from <see cref="Brain"/>.</summary>
    public ENetPacketPeer? Peer;
    /// <summary>The mind of a bot; null for a human player.</summary>
    public BotBrain? Brain;
    public bool IsBot => Brain != null;
    /// <summary>Sequence number of the bot's last input, and whether it was alive last tick (a respawn resets the brain).</summary>
    public ushort BotSeq;
    public bool BotWasAlive;
    /// <summary>This bot carries the RPG as its secondary weapon (#348); decided when it joins.</summary>
    public bool BotLauncher;
    public ushort Id;
    /// <summary>The finds of the Mineral Log were sent to this client (<see cref="NetServer.MineralTick"/> sends them once the book is loaded).</summary>
    public bool FindsSent;
    public string Name = "";
    /// <summary>When this client's last performance report was taken (<see cref="OnClientReport"/>): one per few seconds, the rest are dropped.</summary>
    public double ClientReportAt = -1e9;
    /// <summary>Where this connection is: connected (hello awaited), rejected, loading its profile, joined. <see cref="Joined"/> and <see cref="Loading"/> follow it.</summary>
    public readonly StateMachine<SessionPhase> Session = new(ConnectionPhases.Session);
    public SessionPhase Phase => Session.Current;
    public bool Joined => Session.Is(SessionPhase.Joined);
    /// <summary>The client asked for sandbox mode in its hello (#156).</summary>
    public bool SandboxRequested;
    /// <summary>Sandbox mode granted (the server allows it and the client asked). See <see cref="SandboxRules"/>.</summary>
    public bool Sandbox;
    public Player Body = null!;
    /// <summary>Recoil of this player's trigger hand; the server fires hitscan shots with its offset.</summary>
    public RecoilState Recoil = new(0);
    /// <summary>Sniper breath hold of this player; the server adds its per-shot aim error to hitscan shots.</summary>
    public BreathState Breath = new(0);
    /// <summary>The zero this player dialled on each weapon with bullet drop (<see cref="BulletDrop"/>); a death resets it.</summary>
    public readonly ZeroDial Zero = new();
    /// <summary>The player holds the binoculars at the face (<see cref="Binoculars"/>): what the others are told, nothing the server acts on.</summary>
    public bool BinocularsUp;
    /// <summary>The head lamp is on (<see cref="HeadLamp"/>): told to everyone, nothing else on the server reads it.</summary>
    public bool HeadLampOn;
    public readonly Queue<NetInput> Inputs = new();
    public ushort Ack;
    public bool HasAck;
    public ushort LastSeq;
    public bool HasSeq;
    public byte Slot;
    public PlayerWeapons W = null!;
    /// <summary>This player's Lock (Lock-on launcher, <c>NetServer.LockOn.cs</c>) and what of it was last sent to them (phase, Aircraft, tracking).</summary>
    public readonly LockOn Lock = new();
    public Change<(LockPhase, ushort, bool)> LockSent;
    /// <summary>What the player wears (items.json): set at join, taken off onto the corpse at death, never stored in the profile.</summary>
    public Equipment Worn = null!;
    /// <summary>The blessing and the curse the player carries (Holy Mass reward); ends at death, survives a round change, never stored.</summary>
    public readonly PlayerStatuses Status = new();
    /// <summary>Whether the last <see cref="EventKind.StatusState"/> said this player burns.</summary>
    public bool BurningSent;
    /// <summary>What the player carries unworn: set at join, emptied onto the corpse at death, never stored in the profile (ADR 0018).</summary>
    public Bag Bag = null!;
    /// <summary>The player's perk values. Neutral until the loadout ticket sets them from the player's loadout.</summary>
    public PerkModifiers Perks = PerkModifiers.None;
    /// <summary>XP granted to this player since the round began (the end-of-round summary).</summary>
    public readonly List<XpGrant> RoundXp = new();
    /// <summary>Entity snapshots for this client, against the states the server kept (set in Join).</summary>
    public SnapshotEncoder Enc = null!;
    /// <summary>The newest snapshot tick this client has committed (from its inputs), the baseline for its deltas; NoTick until then.</summary>
    public uint SnapAck = Wire.NoTick;
    /// <summary>World entities for this client, against the world states the server kept (set in Join).</summary>
    public WorldSnapshotEncoder WEnc = null!;
    /// <summary>The newest world tick this client has committed (from its inputs), the baseline for its world deltas; NoTick until then.</summary>
    public uint WAck = Wire.NoTick;
    public Vector3 Start;
    public float MaxMoved;
    /// <summary>Lag compensation (ADR 0013): this player's poses at the end of recent ticks, and the tick the current life (since the last teleport) began.</summary>
    public readonly PoseRing Poses = new();
    public uint LifeStart;
    public int TeleportsSeen;
    public ServerCar? Car;
    /// <summary>The harvester event's driver (#331): a seated bot the AI steers (<c>NetServer.Harvester.cs</c>); the bot roster neither counts nor feeds it.</summary>
    public bool HarvesterDriver;
    /// <summary>Seat of <see cref="Car"/> this player sits in (0 = driver).</summary>
    public int CarSeat;
    /// <summary>A passive rider's look against the hull (<see cref="RiderPose"/>): the turn and the pitch they last sent with the head-look bit, 0 and 0 with it clear. Held yaw is the hull's now plus the turn, so the figure keeps its look as the vehicle turns.</summary>
    public float HeadOff, HeadPitch;
    /// <summary>The flak status last sent to this client as the gunner (magazine, reserve, reloading), so only a change is sent; null until the first.</summary>
    public (int Mag, int Reserve, bool Reloading)? FlakSent;
    public bool LightsHeld, Handbrake;
    /// <summary>Held-key edges of Use and Killstreak. Fed every input (also while dead), so a key held across a respawn is not a fresh press.</summary>
    public Edge UseKey, StreakKey;
    /// <summary>The grenade throw key (G): held to aim, released to throw (<see cref="GrenadeThrower"/>).</summary>
    public readonly GrenadeThrower Throw = new();
    /// <summary>The interact key beside a Scud truck with an empty rack and a loaded rocket cart: a tap gets in or out, a hold loads the rocket (NetServer.Scud.cs).</summary>
    public readonly ScudReloadKey ReloadKey = new();
    /// <summary>The interact key's hold at the rozhlas dictation table (NetServer.RozhlasSet.cs).</summary>
    public InteractHold SetHold;
    /// <summary>The high of the plantation (NetServer.Plantation.cs) and the interact key's hold inside one; the replicated bit is <see cref="StatusKind.High"/> of <see cref="Status"/>.</summary>
    public readonly PlayerHigh High = new();
    public InteractHold PlantHold;
    /// <summary>The use key's hold on lying Loot (NetServer.Thieves.Loot.cs) and the health it was last seen at (a drop is a hit taken).</summary>
    public LootReturnHold LootHold;
    public float LootHealth;
    /// <summary>The Bigfoot holds this player (issue #264) through this tick: their input is read and acknowledged but their body stays where it puts them.</summary>
    public uint HeldUntilTick;
    /// <summary>Until this server time a launched player's fall damage is the Bigfoot's own (the brain hurts them 2 to 5 on landing).</summary>
    public double LaunchGraceUntil;
    /// <summary>Server time of the Bigfoot's launch: until it lands (<see cref="LaunchedAt"/> plus a moment, then on the ground) the player cannot fire (#360).</summary>
    public double LaunchedAt;
    public float Throttle, Steer;
    public PlayerProfile? Profile;
    public ProfileLoad? Loading;
    /// <summary>Server time of the connect, for the hello deadline.</summary>
    public double ConnectedAt;
    public CatchUpBudget CatchUp;
    /// <summary>Ticks a living on-foot player was simulated (one or two inputs) and ticks it had no input queued, so its body stood still (A-08; loss-test telemetry).</summary>
    public int StepTicks, StarvedTicks;
    /// <summary>The profile token from the hello ("" = guest). One live session per token.</summary>
    public string Token = "";
    public int PacketsThisTick;
    public double SaveTokens = 10;
    /// <summary>What this player may send to the chat: a short burst, then about a line a second.</summary>
    public readonly ChatRateLimiter Chat = new();
    public long BytesOut;
}

/// <summary>What the server keeps for a joined player. Persist is false for guests: their changes live only in memory.</summary>
internal sealed class PlayerProfile
{
    public long DbId;
    public bool Persist;
    public string Name = "";
    public int Xp, Level = 1, Active;
    public Loadout[] Loadouts = new Loadout[Loadout.SlotCount];
    public List<Unlock> Unlocks = new();
    /// <summary>One look per loadout slot (#141). The look other players see is the active slot's.</summary>
    public CamoSpec[] Camos = Enumerable.Repeat(CamoSpec.Default, Loadout.SlotCount).ToArray();
    public CamoSpec Camo => Camos[Math.Clamp(Active, 0, Loadout.MaxSlot)];
    /// <summary>The Companion chosen on the Equipment screen (an id of companions.json, #488); empty: none chosen, the first one owned is out.</summary>
    public string Companion = "";
}

/// <summary>A profile still being read on the database thread. Only the task is shared; the result is applied on the tick.</summary>
internal sealed class ProfileLoad
{
    public Task<PlayerProfile> Work = null!;
    public ulong StartedMs;
}
