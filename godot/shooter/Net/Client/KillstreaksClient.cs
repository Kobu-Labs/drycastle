using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Icons;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The client side of killstreaks for the HUD tracker (ticket #119). The tracker mirrors the server's counter: it arms at
/// the spawn, counts the owner's kills from the match feed, resets on death, and takes the server's StreakEarned, StreakUsed
/// and StreakEnded events as authoritative. The Killstreak key itself is already sent in the input packet; the server
/// decides whether a use happens. The gunner streaks' clocks are <see cref="Gunner"/>.
/// </summary>
public sealed class KillstreaksClient
{
    readonly ClientContext _ctx;
    readonly LoadoutClient _loadout;
    KillstreakTracker? _tracker;
    IconRegistry? _icons;

    public KillstreaksClient(ClientContext ctx, LoadoutClient loadout)
    {
        _ctx = ctx;
        _loadout = loadout;
        Gunner = new GunnerStreaks(ctx);
    }

    public GunnerStreaks Gunner { get; }

    /// <summary>The owner's streak was used (the attack missile hands the owner its point of view) or ended (impact, timeout, or death: the view goes back to the body).</summary>
    public event Action<string>? Used, Ended;
    /// <summary>The owner died: the server has ended every streak of theirs, but the views must not depend on that one event arriving (a lost or reordered one left a view stuck on). Idempotent with the real ended events.</summary>
    public event Action? OwnerDied;

    /// <summary>At each spawn (after <see cref="LoadoutClient.ApplySpawn"/>): arms the tracker with the active loadout's streaks.</summary>
    public void Arm()
    {
        if (_ctx.Killstreaks == null) return;
        _tracker ??= new KillstreakTracker(_ctx.Killstreaks);
        var l = _loadout.Active;
        _tracker.Arm(l == null ? Array.Empty<string>() : new[] { l.Streak1, l.Streak2, l.Streak3 }, _loadout.Perks.StreakCostReduction);
        Refresh();
    }

    /// <summary>A round reset drops earned streaks too, as on the server.</summary>
    public void Reset()
    {
        _tracker?.Reset();
        Refresh();
    }

    /// <summary>StreakEarned, StreakUsed and StreakEnded from the server (owner only), and a care package's reward.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.StreakGranted)
        {
            if (KillstreakEvents.DecodeGranted(payload) is { } g) OnRewardGranted(g);
            return;
        }
        if (_tracker == null || kind is not (EventKind.StreakEarned or EventKind.StreakUsed or EventKind.StreakEnded)) return;
        switch (kind)
        {
            case EventKind.StreakEarned when KillstreakEvents.DecodeEarned(payload) is { } e:
                _tracker.OnEarned(e.StreakId, e.Kills);
                break;
            case EventKind.StreakUsed when KillstreakEvents.DecodeUsed(payload) is { } u:
                _tracker.OnUsed(u.StreakId);
                Used?.Invoke(u.StreakId);
                Gunner.OnUsed(u.StreakId);
                break;
            case EventKind.StreakEnded when KillstreakEvents.DecodeEnded(payload) is { } x:
                _tracker.OnEnded(x.StreakId);
                Ended?.Invoke(x.StreakId);
                Gunner.OnEnded(x.StreakId);
                break;
            default:
                return;
        }
        Refresh();
    }

    /// <summary>
    /// A care package reward (ticket #151): a toast names the streak with its icon and the key that uses it. When the reward
    /// slots were full, the toast also names the reward it replaced. Rewards do not show in the loadout tracker.
    /// </summary>
    void OnRewardGranted(StreakGrantedInfo g)
    {
        _icons ??= new IconRegistry(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            _ctx.Killstreaks?.Streaks.Select(s => s.Id) ?? Array.Empty<string>());
        var icon = ResourceLoader.Load<Texture2D>(_icons.Resolve(IconKind.Streak, g.StreakId, p => ResourceLoader.Exists(p)));
        var name = RewardName(g.StreakId);
        var text = g.ReplacedId is { } old
            ? Strings.F("toast.streak.replaced", name, RewardName(old), InputBindings.Name("killstreak"))
            : Strings.F("toast.streak.granted", name, InputBindings.Name("killstreak"));
        UiShell.Instance?.Toast(text, ToastKind.Info, icon);
    }

    string RewardName(string id) => _ctx.Killstreaks?.Find(id)?.Name ?? id;

    /// <summary>A kill from the match feed. The owner's own kills (not suicides, not a streak's weapon) advance the counter.</summary>
    public void OnKill(KillMsg k)
    {
        Gunner.OnKill(k);
        if (_tracker == null || k.Killer != _ctx.MyId || k.Killer == k.Victim) return;
        _tracker.OnKill(StreakWeapons.Is(k.Weapon));
        Refresh();
    }

    /// <summary>The owner died: the counter and the streaks reset, as on the server.</summary>
    public void OnDeath()
    {
        _tracker?.OnDeath();
        OwnerDied?.Invoke();
        Gunner.OnOwnerDied();
        Refresh();
    }

    void Refresh() => _ctx.Hud?.RefreshStreaks(_tracker, !_ctx.Dead);
}
