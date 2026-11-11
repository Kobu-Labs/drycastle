using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class MenuLogicTests
{
    [Fact]
    public void ConnectFormValidates()
    {
        var ok = ConnectForm.Validate(" play.example.cz ", "", "Kuba");
        Assert.True(ok.Ok); Assert.Equal("play.example.cz", ok.Host); Assert.Equal(27015, ok.Port);
        Assert.Equal("connect.err.address.empty", ConnectForm.Validate("", "27015", "a").AddressError);
        Assert.Equal("connect.err.address.bad", ConnectForm.Validate("bad host!", "27015", "a").AddressError);
        Assert.Equal("connect.err.port.bad", ConnectForm.Validate("h", "70000", "a").PortError);
        Assert.Equal("connect.err.port.bad", ConnectForm.Validate("h", "12x", "a").PortError);
        Assert.Equal("connect.err.name.empty", ConnectForm.Validate("h", "1", "  ").NameError);
        Assert.Equal("connect.err.name.long", ConnectForm.Validate("h", "1", new string('x', 25)).NameError);
        var emb = ConnectForm.Validate("10.0.0.5:4000", "27015", "a");
        Assert.True(emb.Ok); Assert.Equal(4000, emb.Port);
    }

    [Fact]
    public void RecentServersAreMruDedupedCappedAndRoundTrip()
    {
        var r = new RecentServers();
        for (var i = 0; i < 9; i++) r.Add($"h{i}:1");
        r.Add("H5:1");
        Assert.Equal(RecentServers.Max, r.Items.Count);
        Assert.Equal("H5:1", r.Items[0]);
        Assert.Single(r.Items, e => e.Equals("h5:1", StringComparison.OrdinalIgnoreCase));
        var back = RecentServers.Parse(r.Serialize());
        Assert.Equal(r.Items, back.Items);
    }

    static BindingMap Map() => new(new[] { ("a", Binding.Key(1)), ("b", Binding.Key(2)), ("c", Binding.MouseButton(1)) });

    [Fact]
    public void AssignSwapsOnConflictAndResets()
    {
        var m = Map();
        Assert.Equal("b", m.Assign("a", Binding.Key(2)));
        Assert.Equal(Binding.Key(2), m.Get("a")); Assert.Equal(Binding.Key(1), m.Get("b"));
        Assert.Null(m.Assign("c", Binding.Key(9)));
        Assert.False(m.IsDefault);
        m.Reset();
        Assert.True(m.IsDefault);
    }

    [Fact]
    public void BindingLoadIgnoresBadOrDuplicateEntries()
    {
        var m = Map();
        m.Load("a", "k:50"); m.Load("b", "garbage"); m.Load("c", "k:50"); m.Load("zzz", "k:7");
        Assert.Equal(Binding.Key(50), m.Get("a"));
        Assert.Equal(Binding.Key(2), m.Get("b"));
        Assert.Equal(Binding.MouseButton(1), m.Get("c"));
        Assert.Equal(Binding.Key(7), Binding.Parse(Binding.Key(7).ToString())!.Value);
    }

    [Fact]
    public void SavedSwapSurvivesLoadAll()
    {
        var m = Map();
        m.Assign("a", Binding.Key(2)); // a<->b swap
        var saved = m.Actions.ToDictionary(x => x, x => (string?)m.Get(x).ToString());
        var loaded = Map();
        loaded.LoadAll(saved);
        Assert.True(loaded.SameAs(m));
    }

    [Fact]
    public void LoadAllRejectsOnlyTrueDuplicates()
    {
        var m = Map();
        m.LoadAll(new Dictionary<string, string?> { ["a"] = "k:50", ["b"] = "k:50", ["c"] = "garbage" });
        Assert.Equal(Binding.Key(50), m.Get("a"));
        Assert.Equal(Binding.Key(2), m.Get("b"));
        Assert.Equal(Binding.MouseButton(1), m.Get("c"));
    }

    [Fact]
    public void LoadAllNeverLeavesTwoActionsOnOneKey()
    {
        var m = Map();
        m.LoadAll(new Dictionary<string, string?> { ["a"] = "k:2" }); // b missing, still defaults to k:2
        Assert.Equal(m.Actions.Count(), m.Actions.Select(m.Get).Distinct().Count());
    }
}
