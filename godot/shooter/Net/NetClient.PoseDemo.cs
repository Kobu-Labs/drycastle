using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// A dev check of the character poses in the real map (<c>--pose-demo</c>): once the local player exists, a row of characters stands a few metres
/// ahead of the spawn, facing the player, each driven (firing ones included) through <see cref="CharacterView.Apply"/> and <see cref="CharacterView.SetStance"/> exactly as a
/// remote player is by the snapshots. They are client-only (no hit volume, no server entity) and never move.
/// </summary>
public partial class NetClient
{
    /// <summary>Set by the host from <c>--pose-demo</c>.</summary>
    public bool PoseDemo;
    bool _poseDemoDone;

    /// <summary>Label, weapon, speed (m/s), firing, dead, stance.</summary>
    static readonly (string Label, string Weapon, float Speed, bool Fire, bool Dead, Stance Stance)[] DemoPoses =
    {
        ("rifle idle", "ak47", 0, false, false, Stance.Stand),
        ("rifle walk", "ak47", 3, false, false, Stance.Stand),
        ("rifle run", "ak47", 7, false, false, Stance.Stand),
        ("rifle firing", "ak47", 0, true, false, Stance.Stand),
        ("rifle firing, walking", "ak47", 3, true, false, Stance.Stand),
        ("pistol firing", "pistol", 0, true, false, Stance.Stand),
        ("pistol idle", "pistol", 0, false, false, Stance.Stand),
        ("knife idle", "knife", 0, false, false, Stance.Stand),
        ("knife swing", "knife", 0, true, false, Stance.Stand),
        ("bazooka idle", "bazooka", 0, false, false, Stance.Stand),
        ("rifle crouch", "ak47", 0, false, false, Stance.Crouch),
        ("rifle prone", "ak47", 0, false, false, Stance.Prone),
        ("no weapon", "", 0, false, false, Stance.Stand),
        ("dead", "ak47", 0, false, true, Stance.Stand),
    };

    void SpawnPoseDemo()
    {
        _poseDemoDone = true;
        var yaw = _me!.Yaw;
        var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        var suit = CamoData.SuitPreset("ghillie_grass")?.Spec ?? CamoSpec.Default;
        for (var i = 0; i < DemoPoses.Length; i++)
        {
            var p = DemoPoses[i];
            var node = new Node3D { Name = $"PoseDemo{i}" };
            var view = CharacterView.Make();
            // every body in turn, a plain suit on the even ones and the ghillie on the odd ones
            var look = (i % 2 == 0 ? CamoSpec.Default : suit) with { Body = (byte)(i % CamoData.Bodies.Count) };
            view.FlashOnShot = true;
            view.SetCamo(look, CamoData);
            view.SetStance(p.Stance);
            view.Apply(p.Speed, p.Fire, p.Dead, p.Weapon);
            if (p.Dead) view.ShowCorpse(p.Weapon);
            node.AddChild(view);
            node.AddChild(new Label3D
            {
                Text = p.Label, Position = new Vector3(0, MoveConsts.CapsuleHeight + 0.3f, 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.005f, NoDepthTest = true,
            });
            World.AddChild(node);
            var along = (i - (DemoPoses.Length - 1) / 2f) * 1.5f;
            node.GlobalPosition = _me.GlobalPosition + forward * 6f + right * along;
            node.Rotation = new Vector3(0, yaw + Mathf.Pi, 0); // facing the player
        }
        GD.Print($"pose-demo: {DemoPoses.Length} characters placed 6 m ahead of the spawn");
    }
}
