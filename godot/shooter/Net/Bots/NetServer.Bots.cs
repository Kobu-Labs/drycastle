using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Bots on the server (#214): players without a connection. A bot is a <see cref="ServerClient"/> with no peer and a
/// <see cref="BotBrain"/>; each tick the brain's command is queued as the bot's input, and from there it is a player like
/// any other: the same movement step, weapons, damage, corpses, kill feed and snapshots, and clients need no change to see
/// it. This file is the bridge between the pure brain in ShooterCore and the match: it keeps the roster filled, answers
/// the brain's questions (who is alive, is the line clear) and tells it when it was hit.
/// <para>
/// Rules: bots are in the match only while a human is (an empty server runs no bot rounds). They count as players for
/// <c>min_players</c>, the score and the round end. They earn no XP and have no stats, and killing one grants no XP and
/// writes no kill stat; the streak counter does advance. They do not use vehicles or killstreaks, and they ignore target
/// dummies and the citizens. A share of them (<see cref="BotTuning.LauncherShare"/>) carries the RPG as its secondary weapon
/// and fires it with a far steadier aim than its gun (#348).
/// </para>
/// <para>
/// Moving: each bot heads for the nearest living enemy (<see cref="BotGoal"/>) along a route from
/// <see cref="BotNav"/>, the navigation mesh baked from the map colliders at start. Routes cost 13-16 ms, so a background worker finds
/// them: a bot sends its request (<see cref="ServeBotRoute"/>), the answers are applied on the tick thread at one point of the tick (<see cref="NavDrain"/>). If the
/// mesh cannot be built the bots stand where they spawn.
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>--bots N: how many bots join while at least one human is in the match. 0 = none.</summary>
    public int Bots;

    /// <summary>Most bots a server takes (they need no peer slot, so this is apart from <see cref="MaxPeers"/>).</summary>
    public const int MaxBots = ChatCommands.MaxBots;

    static readonly string[] BotNames =
    {
        "Adam", "Boris", "Cyril", "Dano", "Emil", "Fero", "Gabo", "Hana", "Ivan", "Jano", "Karol", "Lenka",
        "Milan", "Nora", "Oto", "Palo", "Rasto", "Sona", "Tomas", "Ula", "Viera", "Zuza", "Marek", "Petra",
        "Juro", "Eva", "Miro", "Dasa", "Stano", "Iva", "Lubo", "Olga",
    };

    /// <summary>Every living player as the bots see them, rebuilt once a tick and shared by all the brains.</summary>
    readonly List<BotContact> _botContacts = new();
    /// <summary>Who each bot may fight: the contacts minus the bot's teammates in team deathmatch (<see cref="BotEnemies"/>).</summary>
    readonly BotEnemies _botEnemies = new();
    BotWorld? _botWorld;

    sealed class BotWorld : IBotWorld
    {
        readonly NetServer _s;
        public BotWorld(NetServer server) => _s = server;
        public IReadOnlyList<BotContact> Enemies(ushort self) => _s._botEnemies.For(_s._match, self); // the brain skips itself where the list holds it
        public bool Clear(NVec3 from, NVec3 to) => _s.BotLineClear(from, to);
        public NVec3? Goal(ushort self) => _s.BotGoal(self);
    }

    bool IsBot(ushort id) => _byId.TryGetValue(id, out var c) && c.IsBot;

    /// <summary>The map folder (for the navigation bake: its manifest lists the chunks) and where baked tiles are kept. Set by the host.</summary>
    public string MapDir = "";
    public string NavCacheDir = "";
    BotNav? _nav;
    int _navTurn, _navRoutes, _navDropped;
    double _navMs, _navMaxMs, _navWaitMs, _navWaitMaxMs, _navReportAt;
    long _navTicket;

    /// <summary>Most routes in flight per kind of asker, so bots and Gang members share the worker (<see cref="BotNav.MaxInFlight"/> is the total).</summary>
    const int MaxBotRoutesInFlight = 4, MaxGangRoutesInFlight = 4;

    /// <summary>A ticket for a route request: the microsecond clock, kept strictly rising, so it is unique and the answer's age is read off it.</summary>
    long NextNavTicket() => _navTicket = Math.Max(_navTicket + 1, (long)Time.GetTicksUsec());

    /// <summary>The route worker's finished answers, at one fixed point of the tick (before the entities tick): each goes to the bot or Gang member that asked, or is dropped when none wants it any more.</summary>
    void NavDrain()
    {
        if (_nav == null) return;
        while (_nav.TryTake(out var a))
        {
            var route = ToN(a.Path);
            var applied = false;
            foreach (var b in _byId.Values)
            {
                if (!b.IsBot || b.Brain!.PathTicket != a.Ticket) continue;
                applied = b.BotWasAlive && b.Brain.AcceptPath(a.Ticket, route, N(b.Body.GlobalPosition), _elapsed);
                break;
            }
            if (!applied && _gangs != null) applied = _gangs.DeliverRoute(a.Ticket, route);
            if (!applied) { _navDropped++; continue; }
            var wait = ((long)Time.GetTicksUsec() - a.Ticket) / 1000.0;
            _navRoutes++; _navMs += a.WorkMs; _navMaxMs = Math.Max(_navMaxMs, a.WorkMs);
            _navWaitMs += wait; _navWaitMaxMs = Math.Max(_navWaitMaxMs, wait);
        }
    }

    /// <summary>A route of the worker as the pure side wants it; fewer than two points is no route.</summary>
    static NVec3[] ToN(Vector3[] route)
    {
        if (route.Length < 2) return Array.Empty<NVec3>();
        var r = new NVec3[route.Length];
        for (var i = 0; i < r.Length; i++) r[i] = N(route[i]);
        return r;
    }

    bool _navTried;

    /// <summary>
    /// Once the colliders are in, at start and before connections are accepted: the mesh the bots and the citizens walk on. Not in the
    /// smoke arenas (far from the map). Tried once; it blocks for the bake (once per map version, then it is read from the cache).
    /// </summary>
    void BuildBotNav(bool force = false)
    {
        if (_navTried || (Bots <= 0 && !force) || ArenaMode || MapDir == "") return;
        _navTried = true;
        try
        {
            _nav = BotNav.Build(World, MapDir, NavCacheDir, Math.Max(1, System.Environment.ProcessorCount / 2));
            GD.Print($"server: bot navigation from {_nav.Source}: {_nav.Polygons} polygons in {_nav.Tiles} tiles, {_nav.BuildSeconds:F1} s");
        }
        catch (Exception e)
        {
            GD.PrintErr($"server: bot navigation failed, the bots will stand where they spawn: {e.Message}");
            _nav = null;
        }
    }

    /// <summary>Where the action is for a bot: the nearest living enemy (no teammate in team deathmatch). Null when there is none.</summary>
    NVec3? BotGoal(ushort self) => _botEnemies.Goal(_match, self);

    /// <summary>Sends the waiting route requests to the worker (a bot with one out sends no second; the bots in turn, at most <see cref="MaxBotRoutesInFlight"/> out), and logs the cost of the routes every ten seconds.</summary>
    void ServeBotRoute(List<ServerClient> bots)
    {
        if (_nav == null) return;
        var sent = 0;
        foreach (var b in bots) if (b.Brain!.PathInFlight) sent++;
        var start = _navTurn;
        for (var k = 0; k < bots.Count && sent < MaxBotRoutesInFlight && _nav.HasRoom; k++)
        {
            var b = bots[(start + k) % bots.Count];
            if (!b.BotWasAlive || b.Brain!.PathInFlight || b.Brain.PathRequest is not { } want) continue;
            var ticket = NextNavTicket();
            _nav.Submit(ticket, new Vector3(want.From.X, want.From.Y, want.From.Z), new Vector3(want.To.X, want.To.Y, want.To.Z));
            b.Brain.PathSent(ticket, _elapsed);
            _navTurn = (start + k + 1) % bots.Count;
            sent++;
        }
        if (_elapsed < _navReportAt) return;
        if (_navRoutes > 0 || _navDropped > 0)
            GD.Print($"server: bots {bots.Count}, routes {_navRoutes} in 10 s (dropped {_navDropped}), worker mean {_navMs / Math.Max(1, _navRoutes):F1} ms max {_navMaxMs:F1} ms, wait to applied mean {_navWaitMs / Math.Max(1, _navRoutes):F1} ms max {_navWaitMaxMs:F1} ms");
        // what each bot is doing, and how often it has been blocked this life: the first thing to read when bots misbehave
        GD.Print($"server: bots: {string.Join(", ", bots.Select(b => b.BotWasAlive ? $"{b.Name[4..]} {b.Brain!.Status}{(b.Brain.Stuck > 0 ? $" stuck x{b.Brain.Stuck}" : "")}" : $"{b.Name[4..]} dead"))}");
        _navReportAt = _elapsed + 10; _navRoutes = _navDropped = 0; _navMs = _navMaxMs = _navWaitMs = _navWaitMaxMs = 0;
    }

    /// <summary>Nothing that stops a bullet between the two points: world, vehicles and shot-only cover, as for a shot. Players do not block.</summary>
    bool BotLineClear(NVec3 from, NVec3 to)
    {
        var d = to - from;
        var len = d.Length();
        if (len < 0.01f) return true;
        var dir = d / len;
        var t = Prof.Begin(Prof.S.BotSight);
        var clear = WorldDistance(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len) >= len - 0.05f;
        Prof.End(Prof.S.BotSight, t);
        return clear;
    }

    /// <summary>Once a tick, before the players are simulated: keeps the roster at the wanted size and queues one input per living bot.</summary>
    void BotsTick(float dt)
    {
        if (Bots <= 0 && _byId.Values.All(c => !c.IsBot || c.HarvesterDriver)) return; // no bots wanted and none to send away: the usual server
        var bots = _byId.Values.Where(c => c.IsBot && !c.HarvesterDriver).ToList(); // the harvester's driver is no roster bot (#331)
        var humans = _byId.Values.Count(c => !c.IsBot);
        var want = humans > 0 ? Math.Min(Bots, MaxBots) : 0;
        if (bots.Count < want) AddBot(); // one a tick: a join builds a body
        else if (bots.Count > want)
        {
            var gone = bots[^1];
            bots.RemoveAt(bots.Count - 1);
            GD.Print($"server: bot {gone.Name} #{gone.Id} leaves");
            Leave(gone);
        }
        if (bots.Count == 0) return;

        _botContacts.Clear();
        foreach (var o in _byId.Values)
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive)
                _botContacts.Add(new BotContact(o.Id, N(o.Body.GlobalPosition), _sitters.StanceOf(o), _match.IsProtected(o.Id)));
        _botEnemies.Rebuild(_match, _botContacts);
        _botWorld ??= new BotWorld(this);

        foreach (var b in bots)
        {
            var alive = _match.Get(b.Id)?.Life == PlayerLife.Alive;
            if (alive && !b.BotWasAlive) b.Brain!.Reset((float)(_rng.NextDouble() * Math.Tau), _elapsed); // a new life: no memory of the last one
            b.BotWasAlive = alive;
            if (!alive) continue; // dead or waiting for the round: no input, as a dead player's is ignored
            var w = b.W;
            var self = new BotSelf(b.Id, N(b.Body.GlobalPosition), b.Body.State.Stance, w.Mag(w.Slot), w.Current.MagazineSize, w.Reserve(w.Slot), w.Reloading, w.Current.FullAuto,
                w.Slot, b.BotLauncher ? w.HotbarWeapon(0) + 1 : 0, b.BotLauncher ? BotLauncherOf(w) : null);
            var brainT = Prof.Begin(Prof.S.BotBrain);
            var cmd = b.Brain!.Tick(_elapsed, dt, self, _botWorld);
            Prof.End(Prof.S.BotBrain, brainT);
            var move = new MoveInput { MoveX = cmd.MoveX, MoveY = cmd.MoveY, Yaw = cmd.Yaw, Buttons = cmd.Buttons, SpeedScale = 1f };
            b.Inputs.Enqueue(NetInput.From(++b.BotSeq, move, cmd.Pitch, (byte)cmd.Slot)); // slot 0 (a bot with no launcher): the weapon it spawned with
        }
        ServeBotRoute(bots);
    }

    /// <summary>The weapon slot of the unguided rocket launcher (the RPG: a secondary projectile weapon with a blast and no Lock), 0 when weapons.json has none.</summary>
    int RpgSlot => Weapons.Weapons.FindIndex(x => x.Kind == WeaponKind.Projectile && x.Lock == null && x.Splash != null && x.Slot == WeaponSlot.Secondary) + 1;

    /// <summary>The launcher on a bot's hotbar as its brain sees it, or null: used no closer than its own blast reaches (plus a margin), no farther than the rocket flies or the bot sees.</summary>
    BotLauncher? BotLauncherOf(PlayerWeapons w)
    {
        var slot = RpgSlot;
        if (slot == 0 || !w.OnHotbar(slot)) return null;
        var def = Weapons.Weapons[slot - 1];
        var far = MathF.Min(def.ProjectileSpeed * def.ProjectileLife * 0.9f, BotTuning.Default.SightRangeM);
        return new BotLauncher(slot, w.Mag(slot), w.Reserve(slot), def.Splash!.Radius + 3f, far, def.ProjectileSpeed);
    }

    /// <summary>A new bot: an id, a free name, a guest profile with a random starter loadout, then the same join as a player.</summary>
    void AddBot()
    {
        var id = _ids.Allocate(i => _clients.Values.Any(o => o.Id == i) || _byId.ContainsKey(i));
        if (id == 0) return;
        var taken = _byId.Values.Select(c => c.Name).ToHashSet();
        var name = BotNames.Select(n => "Bot " + n).FirstOrDefault(n => !taken.Contains(n)) ?? $"Bot {id}";
        var c = new ServerClient { Id = id, Name = name, ConnectedAt = _elapsed, Brain = new BotBrain(BotTuning.Default, id) };
        var profile = _profiles.NewGuest();
        profile.Active = _rng.Next(Loadout.SlotCount);
        // a share of the bots carries the RPG instead of a pistol, whatever level gates it for players (#348)
        if (RpgSlot is > 0 and var rpg && _rng.NextDouble() < BotTuning.Default.LauncherShare)
        {
            c.BotLauncher = true;
            for (var i = 0; i < profile.Loadouts.Length; i++) profile.Loadouts[i] = profile.Loadouts[i] with { SecondaryWeapon = Weapons.Weapons[rpg - 1].Id };
        }
        _profiles.Attach(c, profile);
        Join(c);
    }

    /// <summary>A bot that takes damage from another player looks toward where it came from.</summary>
    void BotHit(ushort victim, ushort attacker)
    {
        if (victim == attacker || !_byId.TryGetValue(victim, out var v) || v.Brain == null || !_byId.TryGetValue(attacker, out var a)) return;
        v.Brain.Alert(N(a.Body.GlobalPosition), _elapsed);
    }

    /// <summary>One log line per kill a bot made or suffered, so a server log shows what the bots do.</summary>
    void BotKillLog(PlayerKilled k)
    {
        if (k.Killer == k.Victim || (!IsBot(k.Killer) && !IsBot(k.Victim))) return;
        string Who(ushort id) => _byId.TryGetValue(id, out var c) ? c.Name : $"#{id}";
        GD.Print($"server: {Who(k.Killer)} killed {Who(k.Victim)} (weapon {WeaponKey(k.Weapon)}, {k.Cause})");
    }
}
