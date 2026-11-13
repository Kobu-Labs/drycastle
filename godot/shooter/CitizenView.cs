using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A citizen's body on the client. Everything about the body comes from a <see cref="CitizenModelDef"/> of holy_mass.json, so another model
/// (the Synty peasants, later, from <c>assets-private/</c>) is a data entry plus this one class: <see cref="Build"/> is the only place that loads
/// a model and turns it into a figure. The CC0 animated characters stand in: civilian clothes and skin recoloured from the citizen's look,
/// the helmet hidden, the walk clip, the death clip held on its last frame, and a seated pose made of the rig's sit-down clip held on its
/// last frame (no pew is modelled; the pelvis is put on the seat point). Without a model (headless) a capsule stands in.
/// </summary>
public sealed partial class CitizenView : Node3D
{
    readonly CitizensConfig _cfg;
    readonly CitizenModelDef? _def;
    Node3D? _model;
    AnimationPlayer? _anim;
    byte _shown = 255;

    CitizenView(CitizensConfig cfg, CitizenModelDef def) { _cfg = cfg; _def = def; }

    /// <summary>The figure of the citizen whose look number is <paramref name="look"/>: the body, clothes and skin follow from it, the same on every client.</summary>
    public static CitizenView Make(CitizensConfig cfg, ushort look)
    {
        var h = look * 2654435761u;
        var v = new CitizenView(cfg, cfg.Models[(int)((h >> 8) % cfg.Models.Count)]);
        v.Build(Colour(cfg.Palette[(int)((h >> 12) % cfg.Palette.Count)]), Colour(cfg.SkinTones[(int)((h >> 16) % cfg.SkinTones.Count)]));
        return v;
    }

    static Color Colour(string hex) => Color.FromHtml(hex);

    void Build(Color clothes, Color skin)
    {
        var def = _def!;
        _model = ModelLib.Instance(def.File);
        if (_model == null)
        {
            AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = MoveConsts.CapsuleRadius, Height = def.HeightM }, Position = new Vector3(0, def.HeightM / 2, 0) });
            return;
        }
        _model.Scale = Vector3.One * (def.HeightM / def.ImportHeight);
        _model.RotationDegrees = new Vector3(0, def.YawDeg, 0);
        AddChild(_model);
        _anim = _model.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        if (_anim != null)
        {
            if (_anim.HasAnimation(def.WalkClip)) _anim.GetAnimation(def.WalkClip).LoopMode = Animation.LoopModeEnum.Linear;
            foreach (var clip in new[] { def.DeathClip, def.SitClip })
                if (_anim.HasAnimation(clip)) _anim.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.None;
        }
        if (_model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault() is { Mesh: not null } mi)
        {
            CharacterView.PrepareMesh(mi, def.File); // the skin surface carries vertex colours: dark boots
            var mesh = (ArrayMesh)mi.Mesh;
            var dark = clothes * 0.55f; dark.A = 1f;
            for (var i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                var name = mesh.SurfaceGetName(i);
                Material? m = null;
                if (def.Hide.Contains(name)) m = CharacterView.HiddenMaterial();
                else if (def.Clothes.Contains(name)) m = new StandardMaterial3D { AlbedoColor = clothes, Roughness = 0.9f };
                else if (def.Dark.Contains(name)) m = new StandardMaterial3D { AlbedoColor = dark, Roughness = 0.9f };
                else if (def.Skin.Contains(name)) m = new StandardMaterial3D { AlbedoColor = skin, Roughness = 0.85f, VertexColorUseAsAlbedo = name == "Skin" };
                mi.SetSurfaceOverrideMaterial(i, m);
            }
        }
    }

    /// <summary>Shows what the citizen is doing. <c>Walking</c> and <c>Entering</c> are both the walk clip; a body that is already seated or dead when first seen takes its final pose at once.</summary>
    public void Show(CitizenState state)
    {
        if ((byte)state == _shown) return;
        var first = _shown == 255;
        _shown = (byte)state;
        var def = _def!;
        if (_model == null) return;
        _model.Position = state == CitizenState.Seated ? SeatedOffset(def) : Vector3.Zero;
        var clip = state switch { CitizenState.Seated => def.SitClip, CitizenState.Dead => def.DeathClip, _ => def.WalkClip };
        if (_anim == null || !_anim.HasAnimation(clip) || _anim.CurrentAnimation == clip && _anim.IsPlaying()) return;
        _anim.Play(clip, first ? 0 : 0.15);
        if (first && state is CitizenState.Seated or CitizenState.Dead) _anim.Seek(_anim.GetAnimation(clip).Length, true);
    }

    /// <summary>
    /// Where the model's origin goes so the pelvis of the held sit pose lies on the seat: the feet stand on the floor, the seat is
    /// <see cref="CitizensConfig.SeatHeightM"/> above it (the pelvis a hand's breadth above that), and the clip's pelvis lies behind the origin.
    /// </summary>
    Vector3 SeatedOffset(CitizenModelDef def) => new(0, _cfg.SeatHeightM + 0.1f - def.SitPelvisM, -def.SitBackM);
}
