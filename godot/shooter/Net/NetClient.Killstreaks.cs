using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The client side of killstreaks for the HUD tracker (ticket #119). The tracker mirrors the server's counter: it arms at
/// the spawn, counts the owner's kills from the match feed, resets on death, and takes the server's StreakEarned, StreakUsed
/// and StreakEnded events as authoritative. The Killstreak key itself is already sent in the input packet; the server
/// decides whether a use happens.
/// </summary>
public partial class NetClient
{
    /// <summary>killstreaks.json (set by the host): streak names, thresholds and the tracker rules.</summary>
    public KillstreakFile Killstreaks = null!;

    KillstreakTracker? _streaks;

    /// <summary>At each spawn (from <see cref="ApplySpawnLoadout"/>): arms the tracker with the active loadout's streaks.</summary>
    void ArmStreaks()
    {
        if (Killstreaks == null) return;
        _streaks ??= new KillstreakTracker(Killstreaks);
        var pr = Profile;
        var l = pr is { } p && p.ActiveSlot >= 0 && p.ActiveSlot < p.Loadouts.Count ? p.Loadouts[p.ActiveSlot] : null;
        _streaks.Arm(l == null ? Array.Empty<string>() : new[] { l.Streak1, l.Streak2, l.Streak3 }, Perks.StreakCostReduction);
        RefreshStreaks();
    }

    /// <summary>StreakEarned, StreakUsed and StreakEnded from the server (owner only). Used and ended also reach the streak's own view (the missile's POV).</summary>
    void OnStreakEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.StreakGranted)
        {
            if (KillstreakEvents.DecodeGranted(payload) is { } g) OnRewardGranted(g);
            return;
        }
        if (_streaks == null) return;
        switch (kind)
        {
            case EventKind.StreakEarned when KillstreakEvents.DecodeEarned(payload) is { } e:
                _streaks.OnEarned(e.StreakId, e.Kills);
                break;
            case EventKind.StreakUsed when KillstreakEvents.DecodeUsed(payload) is { } u:
                _streaks.OnUsed(u.StreakId);
                OnStreakUsed(u.StreakId);
                HeliStreakUsed(u.StreakId);
                break;
            case EventKind.StreakEnded when KillstreakEvents.DecodeEnded(payload) is { } x:
                _streaks.OnEnded(x.StreakId);
                OnStreakEnded(x.StreakId);
                HeliStreakEnded(x.StreakId);
                break;
            default:
                return;
        }
        RefreshStreaks();
    }

    IconRegistry? _icons;

    /// <summary>
    /// A care package reward (ticket #151): a toast names the streak with its icon and the key that uses it. When the reward
    /// slots were full, the toast also names the reward it replaced. Rewards do not show in the loadout tracker.
    /// </summary>
    void OnRewardGranted(StreakGrantedInfo g)
    {
        _icons ??= new IconRegistry(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Killstreaks?.Streaks.Select(s => s.Id) ?? Array.Empty<string>());
        var icon = ResourceLoader.Load<Texture2D>(_icons.Resolve(IconKind.Streak, g.StreakId, p => ResourceLoader.Exists(p)));
        var name = RewardStreakName(g.StreakId);
        var text = g.ReplacedId is { } old
            ? Strings.F("toast.streak.replaced", name, RewardStreakName(old), InputBindings.Name("killstreak"))
            : Strings.F("toast.streak.granted", name, InputBindings.Name("killstreak"));
        UiShell.Instance?.Toast(text, ToastKind.Info, icon);
    }

    string RewardStreakName(string id) => Killstreaks?.Find(id)?.Name ?? id;

    /// <summary>A kill from the match feed. The owner's own kills (not suicides, not a streak's weapon) advance the counter.</summary>
    void OnStreakKill(KillMsg k)
    {
        HeliKill(k);
        if (_streaks == null || k.Killer != _myId || k.Killer == k.Victim) return;
        _streaks.OnKill(StreakWeapons.Is(k.Weapon));
        RefreshStreaks();
    }

    /// <summary>The owner died: the counter and the streaks reset, as on the server.</summary>
    void OnStreakDeath()
    {
        _streaks?.OnDeath();
        RefreshStreaks();
    }

    void RefreshStreaks() => _hud?.RefreshStreaks(_streaks, !Dead);
}
