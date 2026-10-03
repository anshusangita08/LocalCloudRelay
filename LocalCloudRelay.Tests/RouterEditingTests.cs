using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// The Routers tab actions. Edit crashed with "Invoke or BeginInvoke cannot be called on a
/// control until the window handle has been created" while Add worked, because only an
/// existing rule starts with rows already ticked - and ticking a row during construction
/// raised the handler before the dialog had a handle.
/// </summary>
public sealed class RouterEditingTests
{
    [Fact]
    public void EveryStrategyExplainsWhatItDoesAndWhenToUseIt()
    {
        foreach (var strategy in RouterStrategies.All)
        {
            Assert.Contains('\n', RouterStrategies.Explain(strategy));
            Assert.Contains("Use ", RouterStrategies.Explain(strategy), StringComparison.Ordinal);
            Assert.NotEqual(strategy, RouterStrategies.Title(strategy));
        }
        // Renamed strategies keep their explanation.
        Assert.Equal(RouterStrategies.Explain(RouterStrategies.FreeFirst), RouterStrategies.Explain("cheapest-first"));
    }

    private static List<RouterEditorForm.RouterPoolEntry> Pool() =>
    [
        new("kimi-k3", "OpenCode Zen", "$3 in / $15 out", ""),
        new("gpt-5.6-luna", "OpenCode Go", "$0.20 in / $1.20 out", ""),
        new("space-bunny-free", "OpenCode Zen", "free", ""),
    ];

    /// <summary>WinForms needs a single-threaded apartment, so construction runs on its own.</summary>
    private static Exception? Construct(Func<Form> build)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { build().Dispose(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) return new TimeoutException("the editor did not finish constructing");
        return failure;
    }

    [Fact]
    public void TheEditorOpensForAnExistingRuleWithoutThrowing()
    {
        // The reported crash: an existing rule arrives with models ticked, and populating
        // the list raises ItemCheck once per row before there is a window handle.
        var failure = Construct(() => new RouterEditorForm(
            RouterRule.Create("localmachine", RouterStrategies.Coding, ["kimi-k3", "deepseek-v4.1-flash"]),
            Pool()));

        Assert.Null(failure);
    }

    [Fact]
    public void TheEditorAlsoOpensForANewRuleAndForAnEmptyRule()
    {
        // Add worked before, but the empty-pool case is the other end of the same path.
        Assert.Null(Construct(() => new RouterEditorForm(null, Pool())));
        Assert.Null(Construct(() => new RouterEditorForm(RouterRule.Create("x", RouterStrategies.Sticky), Pool())));
        // And with nothing to choose from, which is what a fresh install looks like.
        Assert.Null(Construct(() => new RouterEditorForm(null, [])));
    }

    [Fact]
    public void EditingARuleReplacesOnlyThatRule()
    {
        IReadOnlyList<RouterRule> rules =
        [
            RouterRule.Create("code", RouterStrategies.Sticky, ["kimi-k3"]),
            RouterRule.Create("cheap", RouterStrategies.FreeFirst, ["space-bunny-free"]),
            RouterRule.Create("best", RouterStrategies.Premium, ["kimi-k3", "gpt-5.6-luna"]),
        ];

        var updated = MainForm.WithRule(rules, "cheap",
            RouterRule.Create("cheap", RouterStrategies.Coding, ["kimi-k3", "gpt-5.6-luna"]));

        Assert.Equal(3, updated.Count);
        Assert.Equal(RouterStrategies.Coding, updated.Single(r => r.Name == "cheap").Strategy);
        Assert.Equal(2, updated.Single(r => r.Name == "cheap").Pool.Count);
        // The others are untouched, which is the part a record-equality match gets wrong.
        Assert.Equal(RouterStrategies.Sticky, updated.Single(r => r.Name == "code").Strategy);
        Assert.Equal(RouterStrategies.Premium, updated.Single(r => r.Name == "best").Strategy);
    }

    [Fact]
    public void RenamingARuleLeavesExactlyOneBehind()
    {
        IReadOnlyList<RouterRule> rules =
        [
            RouterRule.Create("code", RouterStrategies.Sticky, ["kimi-k3"]),
            RouterRule.Create("cheap", RouterStrategies.FreeFirst, ["space-bunny-free"]),
        ];

        var renamed = MainForm.WithRule(rules, "code", RouterRule.Create("coding", RouterStrategies.Sticky, ["kimi-k3"]));

        Assert.Equal(2, renamed.Count);
        Assert.Contains(renamed, r => r.Name == "coding");
        Assert.DoesNotContain(renamed, r => r.Name == "code");
    }

    [Fact]
    public void RemovingARuleRemovesExactlyOne()
    {
        IReadOnlyList<RouterRule> rules =
        [
            RouterRule.Create("code", RouterStrategies.Sticky, ["kimi-k3"]),
            RouterRule.Create("cheap", RouterStrategies.FreeFirst, ["space-bunny-free"]),
            RouterRule.Create("best", RouterStrategies.Premium, ["kimi-k3"]),
        ];

        var remaining = MainForm.WithoutRule(rules, "cheap");

        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, r => r.Name == "cheap");
        Assert.Contains(remaining, r => r.Name == "code");
        Assert.Contains(remaining, r => r.Name == "best");

        // Removing the last one leaves an empty list rather than throwing.
        Assert.Empty(MainForm.WithoutRule([RouterRule.Create("only", RouterStrategies.Sticky, ["m"])], "only"));
        // And an unknown name is a no-op.
        Assert.Equal(3, MainForm.WithoutRule(rules, "not-there").Count);
    }

    [Fact]
    public void ANameAlreadyInUseIsRefusedButKeepingYourOwnNameIsFine()
    {
        IReadOnlyList<RouterRule> rules =
        [
            RouterRule.Create("code", RouterStrategies.Sticky, ["kimi-k3"]),
            RouterRule.Create("cheap", RouterStrategies.FreeFirst, ["space-bunny-free"]),
        ];

        Assert.True(MainForm.RuleNameTaken(rules, "code"));
        Assert.True(MainForm.RuleNameTaken(rules, "CODE"));       // names are case-insensitive
        Assert.False(MainForm.RuleNameTaken(rules, "coding"));

        // Editing "code" without renaming it must not be refused for colliding with itself.
        Assert.False(MainForm.RuleNameTaken(rules, "code", exceptName: "code"));
        // But renaming it onto another rule's name must be.
        Assert.True(MainForm.RuleNameTaken(rules, "cheap", exceptName: "code"));
    }
}
