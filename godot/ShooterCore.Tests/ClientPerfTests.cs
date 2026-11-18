using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ClientPerfTests
{
    [Fact]
    public void AReportComesBackAsItWasSent()
    {
        var values = new float[ClientReport.Fields.Length];
        for (var i = 0; i < values.Length; i++) values[i] = i * 1.5f;
        var scopes = new float[ClientProf.ScopeCount];
        for (var i = 0; i < scopes.Length; i++) scopes[i] = i + 0.25f;
        var d = ClientReport.Encode(values, scopes, "NVIDIA GeForce RTX 3060 Laptop GPU", "Linux");
        Assert.True(ClientReport.TryDecode(d, out var v, out var s, out var adapter, out var os));
        Assert.Equal(values, v);
        Assert.Equal(scopes, s);
        Assert.Equal("NVIDIA GeForce RTX 3060 Laptop GPU", adapter);
        Assert.Equal("Linux", os);
    }

    [Fact]
    public void AReportThatIsNotWholeIsRefused()
    {
        var d = ClientReport.Encode(new float[] { 1, 2, 3 }, new float[] { 4 }, "gpu", "os");
        for (var n = 0; n < d.Length; n++) Assert.False(ClientReport.TryDecode(d.AsSpan(0, n), out _, out _, out _, out _));
        Assert.False(ClientReport.TryDecode(d.Concat(new byte[] { 0 }).ToArray(), out _, out _, out _, out _));
        var other = (byte[])d.Clone(); other[0] = 99; // another version
        Assert.False(ClientReport.TryDecode(other, out _, out _, out _, out _));
        var many = new byte[] { ClientReport.Version, 255 }; // more values than a report may carry
        Assert.False(ClientReport.TryDecode(many, out _, out _, out _, out _));
    }

    [Fact]
    public void NumbersThatAreNotFiniteArriveAsZeroAndLongTextIsCut()
    {
        var d = ClientReport.Encode(new[] { float.NaN, float.PositiveInfinity, 7f }, Array.Empty<float>(), new string('é', 100), "");
        Assert.True(ClientReport.TryDecode(d, out var v, out _, out var adapter, out _));
        Assert.Equal(new[] { 0f, 0f, 7f }, v);
        Assert.Equal(new string('é', 32), adapter); // 64 bytes, cut between characters
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", "RTX_3060_Laptop")]
    [InlineData("AMD Radeon RX 6600 XT (RADV NAVI23)", "Radeon_RX_6600_XT")]
    [InlineData("Intel(R) UHD Graphics (TGL GT1)", "UHD")]
    [InlineData("", "unknown")]
    public void AnAdapterBecomesAShortLabel(string adapter, string label) => Assert.Equal(label, ClientReport.AdapterLabel(adapter));

    [Fact]
    public void TheFieldsKeepTheirPlaces()
    {
        // the wire carries values by position: a field is added at the end, never moved or removed
        Assert.Equal(0, ClientReport.Index("fps"));
        Assert.Equal(2, ClientReport.Index("frame_p99_ms"));
        Assert.Equal(23, ClientReport.Index("rtt_ms"));
        Assert.Equal(37, ClientReport.Index("driving"));
        Assert.Equal(ClientReport.Fields.Length, ClientReport.Fields.Distinct().Count());
        Assert.Equal("reconcile", ClientProf.Name(8));
        Assert.Equal("net_events", ClientProf.Name(11));
        Assert.Equal((byte)29, (byte)EventKind.ClientReport);
    }

    [Fact]
    public void FrameTimesGiveTheirPercentiles()
    {
        var f = new FrameTimes();
        for (var i = 0; i < 98; i++) f.Add(8.1);
        f.Add(40); f.Add(400);
        Assert.Equal(100, f.Count);
        Assert.Equal(8.25, f.Percentile(50)); // the upper edge of its quarter-millisecond bucket
        Assert.Equal(40.25, f.Percentile(99));
        Assert.Equal(400, f.Percentile(100)); // past the last bucket: the slowest frame itself
        Assert.Equal(400, f.MaxMs);
        Assert.Equal((2, 1, 1), (f.Over33, f.Over50, f.Over100));
        f.Reset();
        Assert.Equal(0, f.Count);
        Assert.Equal(0, f.Percentile(99));
    }

    [Fact]
    public void ALineCanCarryTwoLabels()
    {
        var b = new MetricsBatch();
        b.Begin("drycastle_client", "player", "Jan Novák", "gpu", "RTX_3060").Field("fps", 118.5).End();
        Assert.Equal("drycastle_client,player=Jan_Nov_k,gpu=RTX_3060 fps=118.5\n", b.Take());
    }
}
