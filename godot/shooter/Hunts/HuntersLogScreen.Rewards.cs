using Godot;
using Drycastle.Shooter.Logs;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hunts;

namespace Drycastle.Shooter.Hunts;

/// <summary>The fourth tab: the asker's progress to each collection tier and what each tier gives (#473, docs/hunters-log.md "Rewards").</summary>
public sealed partial class HuntersLogScreen
{
    VBoxContainer _rewardsHost = null!;

    void BuildRewardsPane() => _rewardsHost = NewHost(_panes[3]);

    void RebuildRewards()
    {
        Clear(_rewardsHost);
        switch (Data.Log.Show)
        {
            case LoadView.Spinner:
                _rewardsHost.AddChild(StateBox(Strings.T("log.loading"), Strings.T("log.loading.body"), spinner: true));
                break;
            case LoadView.Error:
                _rewardsHost.AddChild(StateBox(Strings.T("log.error"), Strings.T("log.error.body"), actionText: Strings.T("log.retry"), action: () => Data.RequestLog(0)));
                break;
            case LoadView.Missing:
                _rewardsHost.AddChild(StateBox(Strings.T("log.guest"), Strings.T("log.guest.body"))); // a guest has no log, so no progress
                break;
            default:
                BuildRewardsContent(Data.Log.Data!);
                break;
        }
    }

    void BuildRewardsContent(PlayerLogView log)
    {
        var rewards = _env.Rewards();
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        foreach (var progress in HuntRewards.Progress(log.Life))
            col.AddChild(TierPlate(progress, rewards.Where(r => r.Tier == progress.Tier).ToList()));
        _rewardsHost.AddChild(col);
    }

    /// <summary>
    /// One tier on a metal plate: its medal, its name and "7 / 10" (the Index species with a hunt of that rarity or better), then a line per reward (its icon, its name and
    /// what it is, and "Unlocked" or "Locked"; a tier with none says so). Status is words as well as the plate's lit rim, never colour alone.
    /// </summary>
    Control TierPlate(TierProgress progress, IReadOnlyList<HuntReward> rewards)
    {
        var plate = new LogPlate(Skin) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space);

        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", ShooterTheme.Space);
        head.AddChild(new MedalGlyph(LogSkin.TierColor(progress.Tier)!.Value, Skin.Edge, ShooterTheme.Space * 4));
        var names = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        names.AddThemeConstantOverride("separation", 0);
        names.AddChild(Text(TierName(progress.Tier), "InkBig"));
        var cap = Text(Strings.F("log.rewards.caption", TierName(progress.Tier)), "LogCaption");
        names.AddChild(cap);
        head.AddChild(names);
        var count = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        count.AddThemeConstantOverride("separation", 0);
        count.AddChild(Text(Strings.F("log.rewards.progress", progress.Have, progress.Of), "InkBig", HorizontalAlignment.Right));
        count.AddChild(Text(Strings.T(progress.Reached ? "log.rewards.reached" : "log.rewards.open"), "LogCaption", HorizontalAlignment.Right));
        head.AddChild(count);
        v.AddChild(head);

        if (rewards.Count == 0)
        {
            v.AddChild(Text(Strings.T("log.rewards.soon"), "InkSmall"));
        }
        else
        {
            foreach (var r in rewards) v.AddChild(RewardRow(r, progress.Reached));
        }
        plate.AddChild(v);
        return plate;
    }

    /// <summary>A reward: its icon, its name and what it does on two lines, and "Unlocked" or "Locked" at the end.</summary>
    Control RewardRow(HuntReward r, bool unlocked)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", ShooterTheme.Space);
        var icon = new TextureRect
        {
            Texture = _env.RewardIcon(r), CustomMinimumSize = new Vector2(ShooterTheme.Space * 14, ShooterTheme.Space * 7), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(icon);
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 0);
        text.AddChild(Text(r.Kind == "weapon" ? _env.WeaponName(r.Id) : r.ItemName, "InkText"));
        text.AddChild(Text(Strings.T("log.reward." + r.Id), "InkSmall", wrap: true));
        row.AddChild(text);
        row.AddChild(Text(Strings.T(unlocked ? "log.rewards.unlocked" : "log.rewards.locked"), unlocked ? "InkText" : "InkSmall", HorizontalAlignment.Right));
        return row;
    }
}
