using Drycastle.ShooterCore.Cartel;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>What only the tests ask of a <see cref="DuffelBook"/>.</summary>
static class DuffelBookTestExtensions
{
    /// <summary>How many Duffels are Loaded in the volume (the book is walked by id: a test's entity manager hands them out from 1).</summary>
    public static int LoadedIn(this DuffelBook book, ICargoVolume volume) =>
        Enumerable.Range(1, 0x7FFF).Count(id => book.Find((ushort)id) is { State: DuffelState.Loaded } d && d.Volume == volume);
}
