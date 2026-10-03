using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A provider that starts offering a model should be visible, and the model must not
/// start serving on its own. The list survives restarts, so "new" means new since the
/// last refresh on any earlier launch.
/// </summary>
public sealed class ModelChangeTrackerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "known-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static Dictionary<string, IReadOnlyList<string>> Listing(params (string Provider, string[] Models)[] entries) =>
        entries.ToDictionary(e => e.Provider, e => (IReadOnlyList<string>)e.Models, StringComparer.Ordinal);

    [Fact]
    public void AProviderSeenForTheFirstTimeIsABaselineNotAChange()
    {
        var result = ModelChangeTracker.Track(KnownModels.Empty, Listing(("zen", ["a", "b"])), Now);
        Assert.Equal(0, result.AddedCount);
        Assert.Empty(result.Known.NewFor("zen"));
    }

    [Fact]
    public void ModelsAddedSinceTheLastRefreshAreFlaggedAndCounted()
    {
        var first = ModelChangeTracker.Track(KnownModels.Empty, Listing(("zen", ["a", "b"]), ("go", ["x"])), Now);
        var second = ModelChangeTracker.Track(first.Known, Listing(("zen", ["a", "b", "c", "d"]), ("go", ["x"])), Now.AddHours(1));

        Assert.Equal(2, second.AddedCount);
        Assert.Equal(["c", "d"], second.Added["zen"]);
        Assert.False(second.Added.ContainsKey("go"));
        Assert.True(second.Known.IsNew("zen", "C"));            // case-insensitive, as model ids are
        Assert.Equal(2, second.Known.Providers["zen"].AddedLastRefresh);
    }

    [Fact]
    public void AFlagStaysUntilTheModelIsDecidedOnThenClears()
    {
        var known = ModelChangeTracker.Track(KnownModels.Empty, Listing(("zen", ["a"])), Now).Known;
        known = ModelChangeTracker.Track(known, Listing(("zen", ["a", "b"])), Now).Known;
        known = ModelChangeTracker.Track(known, Listing(("zen", ["a", "b"])), Now).Known;   // next refresh: no change
        Assert.True(known.IsNew("zen", "b"));

        known = ModelChangeTracker.Acknowledge(known, "zen", ["b"]);
        Assert.False(known.IsNew("zen", "b"));
        var again = ModelChangeTracker.Track(known, Listing(("zen", ["a", "b"])), Now);
        Assert.Equal(0, again.AddedCount);                       // known now, never re-flagged
    }

    [Fact]
    public void AnEmptyListingLeavesTheProviderAlone()
    {
        // Offline or signed out: the next real listing must not look entirely new.
        var known = ModelChangeTracker.Track(KnownModels.Empty, Listing(("zen", ["a", "b"])), Now).Known;
        known = ModelChangeTracker.Track(known, Listing(("zen", [])), Now).Known;
        var back = ModelChangeTracker.Track(known, Listing(("zen", ["a", "b"])), Now);
        Assert.Equal(0, back.AddedCount);
    }

    [Fact]
    public void ADroppedModelLosesItsFlagAndRemovedProvidersAreForgotten()
    {
        var known = ModelChangeTracker.Track(KnownModels.Empty, Listing(("zen", ["a"]), ("go", ["x"])), Now).Known;
        known = ModelChangeTracker.Track(known, Listing(("zen", ["a", "b"])), Now).Known;
        known = ModelChangeTracker.Track(known, Listing(("zen", ["a"])), Now).Known;
        Assert.False(known.IsNew("zen", "b"));

        known = ModelChangeTracker.Retain(known, ["zen"]);
        Assert.False(known.Providers.ContainsKey("go"));
    }

    [Fact]
    public void TheListSurvivesARestart()
    {
        var store = new KnownModelsStore(_path);
        var known = ModelChangeTracker.Track(KnownModels.Empty, Listing(("zen", ["a"])), Now).Known;
        known = ModelChangeTracker.Track(known, Listing(("zen", ["a", "b"])), Now).Known;
        store.Save(known);

        var loaded = new KnownModelsStore(_path).Load();
        Assert.True(loaded.IsNew("zen", "b"));
        Assert.Equal(0, ModelChangeTracker.Track(loaded, Listing(("zen", ["a", "b"])), Now).AddedCount);
        Assert.Equal(1, ModelChangeTracker.Track(loaded, Listing(("zen", ["a", "b", "c"])), Now).AddedCount);
    }

    [Fact]
    public void ANewModelIsSwitchedOffByWithModel()
    {
        // MainForm switches every added model off with WithModel(serve: false); the
        // router then never offers it until the operator ticks it.
        var provider = new ProviderSettings("zen", "Zen", ProviderKinds.OpenAi, "https://zen.example/v1", "k", true, 0);
        IReadOnlyList<string> added = ["c", "d"];
        var off = added.Aggregate(provider, (p, m) => p.WithModel(m, serve: false));
        Assert.True(off.ServesModel("a"));
        Assert.False(off.ServesModel("c"));
        Assert.False(off.ServesModel("d"));
    }
}
