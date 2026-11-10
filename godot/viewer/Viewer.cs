using Godot;

/// <summary>Minimal map viewer scene root. Prints a ready message for now.</summary>
public partial class Viewer : Node3D
{
    public override void _Ready()
    {
        GD.Print("viewer ready");
    }
}
