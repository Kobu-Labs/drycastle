using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Ripple rings on the water where a player or boat moves through it. A fixed pool of flat quads, reused: an emit takes a
/// free slot, or the oldest one when all are busy, so nothing is allocated per event (#169). Each ring spreads and fades
/// over about a second in <see cref="_Process"/>. Dropped under reduce motion.
/// </summary>
public partial class WaterRipples : Node3D
{
    const int Slots = 16;
    const string ShaderPath = "res://shaders/water_ripple.gdshader";
    const float Lifetime = 1.0f;
    /// <summary>Above the surface (m), so the ring is not z-fought with the water mesh.</summary>
    const float Lift = 0.02f;

    sealed class Slot { public MeshInstance3D Node = null!; public ShaderMaterial Mat = null!; public float Age; public float Life = Lifetime; public bool Active; }

    readonly Slot[] _slots = new Slot[Slots];
    int _active;

    public WaterRipples()
    {
        var shader = GD.Load<Shader>(ShaderPath);
        var quad = new QuadMesh { Size = Vector2.One };
        for (var i = 0; i < Slots; i++)
        {
            var mat = new ShaderMaterial { Shader = shader };
            var node = new MeshInstance3D { Mesh = quad, MaterialOverride = mat, Name = $"Ripple{i}", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, RotationDegrees = new Vector3(-90f, 0f, 0f) };
            AddChild(node);
            _slots[i] = new Slot { Node = node, Mat = mat };
        }
        SetProcess(false);
    }

    /// <summary>A ring at <paramref name="at"/> (on the surface) spreading to <paramref name="radius"/> m. Skipped under reduce motion or when far from the listener.</summary>
    public void Emit(Vector3 at, float radius, float life = Lifetime)
    {
        if (GameSettings.Current.ReduceMotion || !IsInsideTree()) return;
        if (Sfx.ListenerDistance(at) > EffectCull.EffectRange) return;
        var slot = FreeOrOldest();
        if (!slot.Active) _active++;
        slot.Active = true;
        slot.Age = 0f;
        slot.Life = life;
        slot.Node.Visible = true;
        slot.Node.GlobalPosition = new Vector3(at.X, at.Y + Lift, at.Z);
        slot.Node.Scale = new Vector3(radius * 2f, radius * 2f, 1f); // the quad lies flat: its local y is the ground's z
        slot.Mat.SetShaderParameter("progress", 0f);
        SetProcess(true);
    }

    Slot FreeOrOldest()
    {
        var oldest = _slots[0];
        foreach (var s in _slots)
        {
            if (!s.Active) return s;
            if (s.Age / s.Life > oldest.Age / oldest.Life) oldest = s;
        }
        return oldest;
    }

    public override void _Process(double delta)
    {
        foreach (var s in _slots)
        {
            if (!s.Active) continue;
            s.Age += (float)delta;
            var p = s.Age / s.Life;
            if (p >= 1f)
            {
                s.Active = false;
                s.Node.Visible = false;
                _active--;
                continue;
            }
            s.Mat.SetShaderParameter("progress", p);
        }
        if (_active <= 0) SetProcess(false);
    }
}
