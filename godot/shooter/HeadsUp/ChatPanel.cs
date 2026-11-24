using Godot;
using Drycastle.Shooter.Chat;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Chat;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>The chat window in the HUD (see <see cref="ChatBox"/>): the match owner feeds it lines and takes what the player sends; the client-side commands run here.</summary>
internal sealed class ChatPanel
{
    readonly ChatBox _box;
    readonly Func<ChatContext> _context;
    readonly Action<string> _submit;
    readonly Func<bool> _toggleFps;

    /// <param name="context">Admin rights and spawnable vehicles for the command hints (read when the text changes).</param>
    /// <param name="submit">The player sent a line (a message, or a command that does not run here).</param>
    /// <param name="toggleFps">The /fps command: flips the counter; true when it is on now.</param>
    public ChatPanel(Control root, Func<ChatContext> context, Action<string> submit, Func<bool> toggleFps)
    {
        _context = context; _submit = submit; _toggleFps = toggleFps;
        _box = new ChatBox(context, Run);
        root.AddChild(_box);
    }

    public bool IsOpen => _box.IsOpen;

    public void Open(string prefill) => _box.Open(prefill);

    /// <summary>Closes the chat box. True when it was open.</summary>
    public bool Close() => _box.Close();

    public void AddLine(ChatLine line) => _box.AddLine(line);

    /// <summary>What the player sent: a client-side command (/fps) runs here and never leaves the client; anything else goes to the owner to send.</summary>
    void Run(string text)
    {
        if (text.StartsWith('/') && ChatCommands.Parse(text, _context()) is { Ok: true, Command: { ClientSide: true } cmd } parsed)
        {
            if (cmd.Name == "fps") AddLine(new ChatLine(ChatLineKind.Info, "", Strings.T(_toggleFps() ? "hud2.fps.on" : "hud2.fps.off")));
            else if (cmd.Name == "night") RunNight(parsed.Args);
            return;
        }
        _submit(text);
    }

    /// <summary>TEST /night: tune this client's night lighting in percent; no arguments shows it, reset restores it.</summary>
    void RunNight(IReadOnlyList<string> args)
    {
        if (DayNightCycle.Current is not { } d) return;
        if (args.Count > 0 && args[0] == "reset") d.ResetNightTuning();
        else if (args.Count > 0)
            d.TuneNight(int.Parse(args[0]) / 100f, args.Count > 1 ? int.Parse(args[1]) / 100f : null, args.Count > 2 ? int.Parse(args[2]) / 100f : null);
        AddLine(new ChatLine(ChatLineKind.Info, "", $"night: moon {d.MoonPeak * 100:0}, ambient {d.MinAmbient * 100:0}, exposure {d.NightExposure * 100:0} (percent)"));
    }
}
