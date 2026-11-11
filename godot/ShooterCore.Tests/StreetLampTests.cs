using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class StreetLampTests
{
    static List<Vector3> Row(int n, float spacing) =>
        Enumerable.Range(0, n).Select(i => new Vector3(i * spacing, 0, 0)).ToList();

    static HashSet<int> Lit(int[] slots) => slots.Where(s => s >= 0).ToHashSet();

    [Fact]
    public void LampsAreOffAtDayAndFullOnAtNight()
    {
        Assert.Equal(0f, LampCurve.Level(0f));
        Assert.Equal(0f, LampCurve.Level(0.2f));
        Assert.Equal(1f, LampCurve.Level(0.6f));
        Assert.Equal(1f, LampCurve.Level(1f));
    }

    [Fact]
    public void LevelRisesMonotonicallyThroughDusk()
    {
        float prev = -1f;
        for (int i = 0; i <= 100; i++)
        {
            float v = LampCurve.Level(i / 100f);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
    }

    [Fact]
    public void PoolTakesTheNearestLampsWithinRange()
    {
        var lamps = Row(10, 10f); // x = 0, 10, ..., 90
        var slots = new int[3];
        Array.Fill(slots, -1);
        LampPool.Refresh(slots, lamps, new Vector3(41, 0, 0), 25f);
        // eye at x=41: lamp 4 (x=40) is 1 away, lamp 5 (x=50) 9, lamp 3 (x=30) 11; lamp 6 (x=60) 19 would be 4th
        Assert.Equal(new HashSet<int> { 3, 4, 5 }, Lit(slots));
    }

    [Fact]
    public void LampsBeyondTheMaxDistanceAreNotUsed()
    {
        var lamps = Row(10, 10f);
        var slots = new int[4];
        Array.Fill(slots, -1);
        LampPool.Refresh(slots, lamps, new Vector3(0, 0, 0), 15f);
        Assert.Equal(new HashSet<int> { 0, 1 }, Lit(slots));
        Assert.Equal(2, slots.Count(s => s == -1));
    }

    [Fact]
    public void AssignmentIsStableWhenTheCameraMovesLittle()
    {
        var lamps = Row(20, 10f);
        var slots = new int[2];
        Array.Fill(slots, -1);
        LampPool.Refresh(slots, lamps, new Vector3(52, 0, 0), 200f); // nearest: lamp 5 (x=50), lamp 6 (x=60)
        var before = slots.ToArray();

        // A small move keeps the same two lamps in the same slots: nothing is reassigned.
        bool changed = LampPool.Refresh(slots, lamps, new Vector3(53, 0, 0), 200f);
        Assert.False(changed);
        Assert.Equal(before, slots);
    }

    [Fact]
    public void OnlyTheSlotsWhoseLampLeftTheSetAreReassigned()
    {
        var lamps = Row(20, 10f);
        var slots = new int[2];
        Array.Fill(slots, -1);
        LampPool.Refresh(slots, lamps, new Vector3(0, 0, 0), 200f); // lamps 0 and 1
        Assert.Equal(new[] { 0, 1 }, slots);

        // Walk to x = 150: lamp 15 is on top of the camera and lamp 14 is 10 away; both old slots change.
        bool changed = LampPool.Refresh(slots, lamps, new Vector3(150, 0, 0), 200f);
        Assert.True(changed);
        Assert.Equal(new HashSet<int> { 14, 15 }, Lit(slots));
    }

    [Fact]
    public void KeptLampStaysInItsSlotWhileANewOneTakesTheFreeSlot()
    {
        var lamps = Row(10, 10f);
        var slots = new int[2];
        Array.Fill(slots, -1);
        LampPool.Refresh(slots, lamps, new Vector3(0, 0, 0), 200f); // 0 and 1
        // Camera at x = 12: nearest are lamp 1 (x=10) and lamp 2 (x=20). Lamp 1 keeps slot 1; lamp 0 leaves
        // slot 0, which takes lamp 2.
        LampPool.Refresh(slots, lamps, new Vector3(12, 0, 0), 200f);
        Assert.Equal(2, slots[0]);
        Assert.Equal(1, slots[1]);
    }

    [Fact]
    public void EmptyPoolAndNoLampsAreHarmless()
    {
        Assert.False(LampPool.Refresh(Array.Empty<int>(), Row(3, 1f), Vector3.Zero, 10f));
        var slots = new int[3];
        Array.Fill(slots, -1);
        Assert.False(LampPool.Refresh(slots, new List<Vector3>(), Vector3.Zero, 10f));
        Assert.All(slots, s => Assert.Equal(-1, s));
    }

    [Fact]
    public void EnergyIsFullNearAndZeroFarAndScalesWithLevel()
    {
        Assert.Equal(1f, LampPool.Energy(1f, 0f, 100f));
        Assert.Equal(1f, LampPool.Energy(1f, 60f, 100f));
        Assert.Equal(0f, LampPool.Energy(1f, 100f, 100f));
        Assert.Equal(0f, LampPool.Energy(0f, 1f, 100f));
        float mid = LampPool.Energy(1f, 80f, 100f);
        Assert.InRange(mid, 0.01f, 0.99f);
        Assert.Equal(0f, LampPool.Energy(1f, 1f, 0f));
    }
}
