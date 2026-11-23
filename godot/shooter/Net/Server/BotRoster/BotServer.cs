using Godot;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.Navigation;
using Drycastle.Shooter.Net.Server.Profiles;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.BotRoster;

/// <summary>
/// Bots on the server (#214): players without a connection. A bot is a <see cref="ServerClient"/> with no peer and a
/// <see cref="BotBrain"/>; each tick the brain's command is queued as the bot's input, and from there it is a player like
/// any other: the same movement step, weapons, damage, corpses, kill feed and snapshots, and clients need no change to see
/// it. This class is the bridge between the pure brain in ShooterCore and the match: it keeps the roster filled, answers
/// the brain's questions (who is alive, is the line clear: <see cref="IBotWorld"/>) and tells it when it was hit.
/// <para>
/// Rules: bots are in the match only while a human is (an empty server runs no bot rounds). They count as players for
/// <c>min_players</c>, the score and the round end. They earn no XP and have no stats, and killing one grants no XP and
/// writes no kill stat; the streak counter does advance. They do not use vehicles or killstreaks, and they ignore target
/// dummies and the citizens. A share of them (<see cref="BotTuning.LauncherShare"/>) carries the RPG as its secondary weapon
/// and fires it with a far steadier aim than its gun (#348).
/// </para>
/// <para>
/// Moving: each bot heads for the nearest living enemy (<see cref="Goal"/>) along a route from the navigation mesh (<see cref="NavService"/>).
/// Routes cost 13-16 ms, so a background worker finds them: a bot sends its request (<see cref="ServeRoutes"/>), the answers are applied on the tick thread at one point of the
/// tick (<see cref="NavService.Drain"/>). If the mesh cannot be built the bots stand where they spawn.
/// </para>
/// </summary>
internal sealed class BotServer : IBotWorld
{
    /// <summary>Most routes in flight per kind of asker, so bots and Gang members share the worker (<c>BotNav.MaxInFlight</c> is the total).</summary>
    const int MaxRoutesInFlight = 4;

    static readonly string[] Names =
    {
        "Adam", "Boris", "Cyril", "Dano", "Emil", "Fero", "Gabo", "Hana", "Ivan", "Jano", "Karol", "Lenka",
        "Milan", "Nora", "Oto", "Palo", "Rasto", "Sona", "Tomas", "Ula", "Viera", "Zuza", "Marek", "Petra",
        "Juro", "Eva", "Miro", "Dasa", "Stano", "Iva", "Lubo", "Olga",
    };

    readonly ServerContext _ctx;
    readonly NavService _nav;
    readonly ProfileServer _profiles;
    readonly ProgressionServer _progress;
    readonly Sitters _sitters;
    readonly WeaponsFile _weapons;
    readonly Random _rng;
    readonly Func<ushort> _newId;
    readonly Action<ServerClient> _join;
    readonly Action<ServerClient> _leave;
    readonly Func<Vector3, Vector3, float, float> _worldDistance;

    /// <summary>Every living player as the bots see them, rebuilt once a tick and shared by all the brains.</summary>
    readonly List<BotContact> _contacts = new();
    /// <summary>Who each bot may fight: the contacts minus the bot's teammates in team deathmatch (<see cref="BotEnemies"/>).</summary>
    readonly BotEnemies _enemies = new();
    int _wanted;
    int _routeTurn;
    double _reportAt;

    /// <param name="wanted">--bots N: how many bots join while at least one human is in the match. 0 = none; an admin changes it.</param>
    /// <param name="newId">A free player id (0 when there is none).</param>
    /// <param name="join">The same join as a player's, once the bot has its profile.</param>
    /// <param name="leave">Takes a bot that is no longer wanted out of the match.</param>
    /// <param name="worldDistance">Distance along a ray to the first thing a shot stops on.</param>
    public BotServer(ServerContext ctx, NavService nav, ProfileServer profiles, ProgressionServer progress, Sitters sitters, WeaponsFile weapons, Random rng, int wanted,
        Func<ushort> newId, Action<ServerClient> join, Action<ServerClient> leave, Func<Vector3, Vector3, float, float> worldDistance)
    {
        _ctx = ctx; _nav = nav; _profiles = profiles; _progress = progress; _sitters = sitters; _weapons = weapons; _rng = rng; _wanted = wanted;
        _newId = newId; _join = join; _leave = leave; _worldDistance = worldDistance;
    }

    // ---- what the brains ask (IBotWorld) ----

    public IReadOnlyList<BotContact> Enemies(ushort self) => _enemies.For(_ctx.Match, self); // the brain skips itself where the list holds it

    /// <summary>Nothing that stops a bullet between the two points: world, vehicles and shot-only cover, as for a shot. Players do not block.</summary>
    public bool Clear(NVec3 from, NVec3 to)
    {
        var d = to - from;
        var len = d.Length();
        if (len < 0.01f) return true;
        var dir = d / len;
        var t = Prof.Begin(Prof.S.BotSight);
        var clear = _worldDistance(from.ToGodot(), dir.ToGodot(), len) >= len - 0.05f;
        Prof.End(Prof.S.BotSight, t);
        return clear;
    }

    /// <summary>Where the action is for a bot: the nearest living enemy (no teammate in team deathmatch). Null when there is none.</summary>
    public NVec3? Goal(ushort self) => _enemies.Goal(_ctx.Match, self);

    // ---- the tick ----

    /// <summary>Once a tick, before the players are simulated: keeps the roster at the wanted size and queues one input per living bot.</summary>
    public void Tick(float dt)
    {
        var players = _ctx.Players;
        if (_wanted <= 0 && players.Values.All(c => !c.IsBot || c.HarvesterDriver)) return; // no bots wanted and none to send away: the usual server
        var bots = players.Values.Where(c => c.IsBot && !c.HarvesterDriver).ToList(); // the harvester's driver is no roster bot (#331)
        var humans = players.Values.Count(c => !c.IsBot);
        var want = humans > 0 ? Math.Min(_wanted, ChatCommands.MaxBots) : 0;
        if (bots.Count < want) AddBot(); // one a tick: a join builds a body
        else if (bots.Count > want)
        {
            var gone = bots[^1];
            bots.RemoveAt(bots.Count - 1);
            GD.Print($"server: bot {gone.Name} #{gone.Id} leaves");
            _leave(gone);
        }
        if (bots.Count == 0) return;

        _contacts.Clear();
        foreach (var o in players.Values)
            if (_ctx.IsAlive(o.Id))
                _contacts.Add(new BotContact(o.Id, o.Body.GlobalPosition.ToNumerics(), _sitters.StanceOf(o), _ctx.Match.IsProtected(o.Id)));
        _enemies.Rebuild(_ctx.Match, _contacts);

        foreach (var b in bots) QueueInput(b, dt);
        ServeRoutes(bots);
    }

    /// <summary>One living bot: its brain looks at the contacts and the world, and the command it chooses is queued as the input of this tick.</summary>
    void QueueInput(ServerClient b, float dt)
    {
        var alive = _ctx.IsAlive(b.Id);
        if (alive && !b.BotWasAlive) b.Brain!.Reset((float)(_rng.NextDouble() * Math.Tau), _ctx.Now); // a new life: no memory of the last one
        b.BotWasAlive = alive;
        if (!alive) return; // dead or waiting for the round: no input, as a dead player's is ignored
        var w = b.W;
        var self = new BotSelf(b.Id, b.Body.GlobalPosition.ToNumerics(), b.Body.State.Stance, w.Mag(w.Slot), w.Current.MagazineSize, w.Reserve(w.Slot), w.Reloading, w.Current.FullAuto,
            w.Slot, b.BotLauncher ? w.HotbarWeapon(0) + 1 : 0, b.BotLauncher ? LauncherOf(w) : null);
        var brainT = Prof.Begin(Prof.S.BotBrain);
        var cmd = b.Brain!.Tick(_ctx.Now, dt, self, this);
        Prof.End(Prof.S.BotBrain, brainT);
        var move = new MoveInput { MoveX = cmd.MoveX, MoveY = cmd.MoveY, Yaw = cmd.Yaw, Buttons = cmd.Buttons, SpeedScale = 1f };
        b.Inputs.Enqueue(NetInput.From(++b.BotSeq, move, cmd.Pitch, (byte)cmd.Slot)); // slot 0 (a bot with no launcher): the weapon it spawned with
    }

    /// <summary>Sends the waiting route requests to the worker (a bot with one out sends no second; the bots in turn, at most <see cref="MaxRoutesInFlight"/> out), and logs the cost of the routes every ten seconds.</summary>
    void ServeRoutes(List<ServerClient> bots)
    {
        if (_nav.Mesh is not { } nav) return;
        var sent = 0;
        foreach (var b in bots) if (b.Brain!.PathInFlight) sent++;
        var start = _routeTurn;
        for (var k = 0; k < bots.Count && sent < MaxRoutesInFlight && nav.HasRoom; k++)
        {
            var b = bots[(start + k) % bots.Count];
            if (!b.BotWasAlive || b.Brain!.PathInFlight || b.Brain.PathRequest is not { } want) continue;
            var ticket = _nav.NextTicket();
            nav.Submit(ticket, want.From.ToGodot(), want.To.ToGodot());
            b.Brain.PathSent(ticket, _ctx.Now);
            _routeTurn = (start + k + 1) % bots.Count;
            sent++;
        }
        if (_ctx.Now < _reportAt) return;
        if (_nav.TakeSummary(bots.Count) is { } routes) GD.Print(routes);
        // what each bot is doing, and how often it has been blocked this life: the first thing to read when bots misbehave
        GD.Print($"server: bots: {string.Join(", ", bots.Select(b => b.BotWasAlive ? $"{b.Name[4..]} {b.Brain!.Status}{(b.Brain.Stuck > 0 ? $" stuck x{b.Brain.Stuck}" : "")}" : $"{b.Name[4..]} dead"))}");
        _reportAt = _ctx.Now + 10;
    }

    // ---- the roster ----

    /// <summary>The weapon slot of the unguided rocket launcher (the RPG: a secondary projectile weapon with a blast and no Lock), 0 when weapons.json has none.</summary>
    int RpgSlot => _weapons.Weapons.FindIndex(x => x.Kind == WeaponKind.Projectile && x.Lock == null && x.Splash != null && x.Slot == WeaponSlot.Secondary) + 1;

    /// <summary>The launcher on a bot's hotbar as its brain sees it, or null: used no closer than its own blast reaches (plus a margin), no farther than the rocket flies or the bot sees.</summary>
    BotLauncher? LauncherOf(PlayerWeapons w)
    {
        var slot = RpgSlot;
        if (slot == 0 || !w.OnHotbar(slot)) return null;
        var def = _weapons.Weapons[slot - 1];
        var far = MathF.Min(def.ProjectileSpeed * def.ProjectileLife * 0.9f, BotTuning.Default.SightRangeM);
        return new BotLauncher(slot, w.Mag(slot), w.Reserve(slot), def.Splash!.Radius + 3f, far, def.ProjectileSpeed);
    }

    /// <summary>A new bot: an id, a free name, a guest profile with a random starter loadout, then the same join as a player.</summary>
    void AddBot()
    {
        var id = _newId();
        if (id == 0) return;
        var taken = _ctx.Players.Values.Select(c => c.Name).ToHashSet();
        var name = Names.Select(n => "Bot " + n).FirstOrDefault(n => !taken.Contains(n)) ?? $"Bot {id}";
        var c = new ServerClient { Id = id, Name = name, ConnectedAt = _ctx.Now, Brain = new BotBrain(BotTuning.Default, id) };
        var profile = _profiles.NewGuest();
        profile.Active = _rng.Next(Loadout.SlotCount);
        // a share of the bots carries the RPG instead of a pistol, whatever level gates it for players (#348)
        if (RpgSlot is > 0 and var rpg && _rng.NextDouble() < BotTuning.Default.LauncherShare)
        {
            c.BotLauncher = true;
            for (var i = 0; i < profile.Loadouts.Length; i++) profile.Loadouts[i] = profile.Loadouts[i] with { SecondaryWeapon = _weapons.Weapons[rpg - 1].Id };
        }
        _profiles.Attach(c, profile);
        _join(c);
    }

    /// <summary>A bot that takes damage from another player looks toward where it came from.</summary>
    public void OnHit(ushort victim, ushort attacker)
    {
        var players = _ctx.Players;
        if (victim == attacker || !players.TryGetValue(victim, out var v) || v.Brain == null || !players.TryGetValue(attacker, out var a)) return;
        v.Brain.Alert(a.Body.GlobalPosition.ToNumerics(), _ctx.Now);
    }

    /// <summary>One log line per kill a bot made or suffered, so a server log shows what the bots do.</summary>
    public void LogKill(PlayerKilled k)
    {
        if (k.Killer == k.Victim || (!IsBot(k.Killer) && !IsBot(k.Victim))) return;
        string Who(ushort id) => _ctx.Players.TryGetValue(id, out var c) ? c.Name : $"#{id}";
        GD.Print($"server: {Who(k.Killer)} killed {Who(k.Victim)} (weapon {_progress.WeaponKey(k.Weapon)}, {k.Cause})");
    }

    bool IsBot(ushort id) => _ctx.Players.TryGetValue(id, out var c) && c.IsBot;

    // ---- the admin's command ----

    /// <summary>"/bots [n]": tells how many bots are wanted and in the match, or sets the number; the bots join and leave from the next tick.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        var live = _ctx.Players.Values.Count(o => o.IsBot);
        if (args.Count == 0)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, $"Bots: {_wanted} wanted, {live} in the match (at most {ChatCommands.MaxBots}).");
            return;
        }
        var n = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        if (n == _wanted) { _ctx.ChatTo(c, ChatLineKind.Info, $"Bots are already {n}."); return; }
        _wanted = n; // Tick reads this every tick: bots join and leave from the next one
        if (n > 0 && _nav.Mesh == null && _nav.CanBuild)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, "Building the bots' navigation for the first time. The server pauses for a few seconds.");
            _nav.BuildAfterNotice();
            _ctx.ChatTo(c, ChatLineKind.Info, _nav.Mesh is { } mesh ? $"Bot navigation ready in {mesh.BuildSeconds:F1} s." : "Bot navigation failed: the bots will stand where they spawn. See the server log.");
        }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} set the bots to {n}.");
        GD.Print($"server: bots set to {n} by {c.Name} #{c.Id}");
    }
}
