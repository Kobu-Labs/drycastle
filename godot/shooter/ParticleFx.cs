using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Keeps idle CPU particle emitters from being drawn. The engine allocates an emitter's instance buffer without contents when the emitter is
/// made and again every time <c>Amount</c> is set, and marks every instance of it visible (CPUParticles3D::set_amount). The buffer is only
/// filled when the emitter emits. An emitter that sits in the tree and has not emitted since (a pool member not used yet, a damage emitter
/// waiting for its stage, a far vehicle's smoke whose amount changed) is therefore drawn with whatever the graphics memory held: nothing while
/// that memory is zero, huge stretched copies of its mesh once the memory has been used before (many effects made and freed).
/// So an emitter is hidden until it emits: make it with <c>Visible = false</c>, start it with <see cref="Fire"/> or <see cref="Run"/>, and
/// change its amount with <see cref="SetAmount"/>. An emitter that has emitted and run out is safe: the engine stops drawing it itself.
/// </summary>
public static class ParticleFx
{
    /// <summary>One burst from a one-shot emitter (a pool member): shown, then restarted, which fills its buffer before the next frame is drawn.</summary>
    public static void Fire(CpuParticles3D p)
    {
        p.Visible = true;
        p.Restart();
    }

    /// <summary>Turns a continuous emitter on or off. Off leaves what is in the air to die out; on shows an emitter that was kept hidden.</summary>
    public static void Run(CpuParticles3D p, bool on)
    {
        if (!on) { p.Emitting = false; return; }
        if (p.Visible) { p.Emitting = true; return; }
        p.Visible = true;
        p.Restart(); // not just Emitting = true: the restart fills the buffer now, whatever state the emitter was hidden in
    }

    /// <summary>Sets the particle count. The engine drops the particles and the buffer with it: an emitting emitter starts over at once, an idle one is hidden until it runs.</summary>
    public static void SetAmount(CpuParticles3D p, int amount)
    {
        if (p.Amount == amount) return;
        p.Amount = amount;
        if (p.Emitting && p.Visible) p.Restart();
        else p.Visible = false;
    }
}
