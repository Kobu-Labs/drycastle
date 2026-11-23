using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server;

public partial class NetServer
{
    /// <summary>animals.json: the species table (set by the host).</summary>
    public AnimalsFile AnimalData = new();
    /// <summary>The hit shapes of the wild species (set by the host): what a bullet is tested against.</summary>
    public AnimalShapes AnimalShapes = new();
    /// <summary>pets.json: which groups keep pets and the poop rules (set by the host).</summary>
    public PetsFile PetsData = new();
}
