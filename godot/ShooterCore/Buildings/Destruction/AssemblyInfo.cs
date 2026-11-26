using System.Runtime.CompilerServices;

// HouseCells.Remove and the varint helper are internal; the unit tests set removed cells without a carve (WP-B's support tests).
[assembly: InternalsVisibleTo("ShooterCore.Tests")]

namespace Drycastle.ShooterCore.Buildings.Destruction;
