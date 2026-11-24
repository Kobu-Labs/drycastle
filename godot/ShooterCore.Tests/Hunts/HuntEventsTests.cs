using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Net;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Hunts;

/// <summary>The Hunter's Log wire (#336): kinds 100-106 round-trip and refuse malformed payloads.</summary>
public class HuntEventsTests
{
    static readonly HuntView Full = new(42, "Anička", "moose", "moose", "moose_male_01", 512.3f, 187.5f, "sniper", new HitDiagram(10, 200, true, true, 3, 9), 1_780_000_000, false);
    static readonly HuntView Run = new(7, "Bob", "deer", "doe", "", 61.5f, null, HuntWeapons.RunOver, null, 1_780_000_100, false);
    static readonly HuntView Hid = new(8, "Cy", "mystery-1", "", "", null, 12f, "ak47", null, 1_780_000_200, true);

    [Fact]
    public void KindsAreTheOnesInTheDocAndUnique()
    {
        Assert.Equal(new byte[] { 100, 101, 102, 103, 104, 105, 106 }, new[]
        {
            EventKind.HuntsRequest, EventKind.HuntsPage, EventKind.RecordsRequest, EventKind.Records, EventKind.PlayerLogRequest, EventKind.PlayerLog, EventKind.HuntAnnounce,
        }.Select(k => (byte)k));
        Assert.True(Wire.ProtocolVersion >= 62);
    }

    [Fact]
    public void RequestRoundTrips()
    {
        var r = new HuntsRequest(513, 3, HuntSort.Kg, true, "deer", "sniper", 99);
        Assert.Equal(r, HuntEvents.DecodeRequest(HuntEvents.EncodeRequest(r)));
        var none = new HuntsRequest(1, 0, HuntSort.Time, false, "", "", 0);
        Assert.Equal(none, HuntEvents.DecodeRequest(HuntEvents.EncodeRequest(none)));
        Assert.Null(HuntEvents.DecodeRequest(new byte[] { 1 }));
        var bad = HuntEvents.EncodeRequest(r); bad[4] = 99; // no such sort column
        Assert.Null(HuntEvents.DecodeRequest(bad));
        Assert.Null(HuntEvents.DecodeRequest(HuntEvents.EncodeRequest(r).Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void PageRoundTripsWithEveryNullableField()
    {
        var a = new HuntsAnswer(9, HuntStatus.Ok, 42, 2, 123, new[] { Full, Run, Hid });
        var back = HuntEvents.DecodePage(HuntEvents.EncodePage(a))!;
        Assert.Equal((a.Req, a.Status, a.ViewerId, a.Page, a.Total), (back.Req, back.Status, back.ViewerId, back.Page, back.Total));
        Assert.Equal(a.Rows, back.Rows);
        var err = new HuntsAnswer(9, HuntStatus.Error, 0, 0, 0, Array.Empty<HuntView>());
        Assert.Equal(HuntStatus.Error, HuntEvents.DecodePage(HuntEvents.EncodePage(err))!.Status);
        var bytes = HuntEvents.EncodePage(a);
        Assert.Null(HuntEvents.DecodePage(bytes[..^3]));
    }

    [Fact]
    public void RecordsRoundTrip()
    {
        Assert.Equal((ushort)777, HuntEvents.DecodeRecordsRequest(HuntEvents.EncodeRecordsRequest(777)));
        Assert.Null(HuntEvents.DecodeRecordsRequest(new byte[3]));
        var a = new RecordsAnswer(5, HuntStatus.Ok, 3, new[]
        {
            new SpeciesRecordView("moose", false, true, Full, Full, Full),
            new SpeciesRecordView("mystery-1", true, true, null, null, Hid),
            new SpeciesRecordView("wolf", false, false, null, null, null),
        });
        var back = HuntEvents.DecodeRecords(HuntEvents.EncodeRecords(a))!;
        Assert.Equal(a.Records, back.Records);
        Assert.Equal((5, 3L), ((int)back.Req, back.ViewerId));
        Assert.Null(HuntEvents.DecodeRecords(HuntEvents.EncodeRecords(a)[..^1]));
    }

    [Fact]
    public void PlayerLogRoundTripsAndAnUnknownPlayerCarriesNoLog()
    {
        var req = new PlayerLogRequest(4, 12);
        Assert.Equal(req, HuntEvents.DecodePlayerRequest(HuntEvents.EncodePlayerRequest(req)));
        Assert.Null(HuntEvents.DecodePlayerRequest(new byte[9]));
        var log = new PlayerLogView(42, "Anička", 17, 3, Full, null, new[]
        {
            new LifeEntry("moose", false, true, 512.3f, 187.5f, 1_780_000_000),
            new LifeEntry("mystery-1", true, true, null, null, 0),
            new LifeEntry("wolf", false, false, null, null, 0),
        });
        var a = new PlayerLogAnswer(6, HuntStatus.Ok, 42, log);
        var back = HuntEvents.DecodePlayerLog(HuntEvents.EncodePlayerLog(a))!;
        Assert.Equal(log with { Life = Array.Empty<LifeEntry>() }, back.Log! with { Life = Array.Empty<LifeEntry>() });
        Assert.Equal(log.Life, back.Log!.Life);
        var unknown = HuntEvents.DecodePlayerLog(HuntEvents.EncodePlayerLog(new PlayerLogAnswer(6, HuntStatus.Unknown, 42, null)))!;
        Assert.Equal((HuntStatus.Unknown, (PlayerLogView?)null), (unknown.Status, unknown.Log));
    }

    [Fact]
    public void AnnounceRoundTripsWithItsFlags()
    {
        var m = new HuntAnnounceMsg(HuntAnnounceFlags.First | HuntAnnounceFlags.Heaviest, Full);
        Assert.Equal(m, HuntEvents.DecodeAnnounce(HuntEvents.EncodeAnnounce(m)));
        var hidden = new HuntAnnounceMsg(HuntAnnounceFlags.First, Hid);
        Assert.Equal(hidden, HuntEvents.DecodeAnnounce(HuntEvents.EncodeAnnounce(hidden)));
        var bad = HuntEvents.EncodeAnnounce(m); bad[0] = 200;
        Assert.Null(HuntEvents.DecodeAnnounce(bad));
    }

    [Fact]
    public void ALongStringIsClampedWithoutCuttingACharacterAndTheEnvelopeCarriesIt()
    {
        var long_ = new HuntView(1, new string('č', 200), "deer", "deer", "", 1, 1, "x", null, 0, false);
        var back = HuntEvents.DecodeAnnounce(HuntEvents.EncodeAnnounce(new HuntAnnounceMsg(HuntAnnounceFlags.Longest, long_)))!;
        Assert.True(back.Hunt.Hunter.Length <= 127);
        Assert.All(back.Hunt.Hunter, c => Assert.Equal('č', c));
        var env = new EventMsg((byte)EventKind.HuntAnnounce, HuntEvents.EncodeAnnounce(new HuntAnnounceMsg(HuntAnnounceFlags.Longest, Full)));
        var round = (EventMsg)Wire.DecodeReliable(Wire.Encode(env))!;
        Assert.Equal(Full, HuntEvents.DecodeAnnounce(round.Payload)!.Hunt);
    }
}
