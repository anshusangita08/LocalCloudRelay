using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A router is a name backed by a pool the operator chose, plus a strategy that picks
/// within it. Two properties matter: it never reaches outside the pool, and the strategy
/// decides the order rather than the catalog doing it.
/// </summary>
public sealed class RouterRuleTests
{
    private static ProviderSettings Provider(string id, int priority = 0) =>
        new(id, id, ProviderKinds.OpenAi, $"https://{id}.example/v1", "k", true, priority);

    /// <summary>zen carries the paid and free models the price tables know.</summary>
    private static ProviderRouter Router(RouterRule[] rules, RouterEngine? engine = null, params (ProviderSettings Provider, string[] Models)[] entries)
    {
        var catalog = new CatalogSnapshot(DateTimeOffset.UtcNow,
            entries.ToDictionary(e => e.Provider.Id, e => (IReadOnlyList<string>)e.Models, StringComparer.Ordinal));
        return new ProviderRouter(entries.Select(e => e.Provider), catalog, rules);
    }

    private static (ProviderSettings Provider, string[] Models) Zen(params string[] models) => (Provider("zen"), models);

    [Fact]
    public void ARouterOnlyEverPicksFromItsOwnPool()
    {
        // The whole point: the catalog has five models, the router was given two, and the
        // cheapest of the five is not in the pool.
        var router = Router([RouterRule.Create("mine", RouterStrategies.FreeFirst, ["kimi-k3", "claude-opus-4-5"])],
            null, Zen("kimi-k3", "claude-opus-4-5", "gpt-5.6-luna", "space-bunny-free", "gpt-5-nano"));

        Assert.True(router.TryResolve("mine", out var route, new RouterEngine(), "s1"));
        Assert.NotEqual("gpt-5-nano", route.Model);
        Assert.True(route.Model is "kimi-k3" or "claude-opus-4-5");
    }

    [Fact]
    public void PremiumPicksTheDearestInThePool()
    {
        var router = Router([RouterRule.Create("best", RouterStrategies.Premium, ["kimi-k3", "gpt-5.6-luna", "claude-opus-4-5"])],
            null, Zen("kimi-k3", "gpt-5.6-luna", "claude-opus-4-5"));

        Assert.True(router.TryResolve("best", out var route, new RouterEngine(), "s1"));
        Assert.Equal("claude-opus-4-5", route.Model);   // 5/25
    }

    [Fact]
    public void FreeFirstPicksTheCheapestWhenNothingIsFree()
    {
        var router = Router([RouterRule.Create("value", RouterStrategies.FreeFirst, ["kimi-k3", "gpt-5.6-luna", "claude-opus-4-5"])],
            null, Zen("kimi-k3", "gpt-5.6-luna", "claude-opus-4-5"));

        Assert.True(router.TryResolve("value", out var route, new RouterEngine(), "s1"));
        Assert.Equal("gpt-5.6-luna", route.Model);      // 0.20/1.20
    }

    [Fact]
    public void RoundRobinRotatesThroughThePool()
    {
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("rr", RouterStrategies.RoundRobin, ["a-model", "b-model", "c-model"])],
            null, Zen("a-model", "b-model", "c-model"));

        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            Assert.True(router.TryResolve("rr", out var route, engine, $"s{i}"));
            seen.Add(route.Model);
        }

        // Every model gets a turn across conversations, and the cycle repeats.
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(seen.Take(3).OrderBy(x => x), seen.Skip(3).OrderBy(x => x));
    }

    [Fact]
    public void RoundRobinKeepsAWarmConversationAndBalancesConversations()
    {
        var providerA = Provider("provider-a", 0);
        var providerB = Provider("provider-b", 1);
        var router = Router([RouterRule.Create("rr", RouterStrategies.RoundRobin, ["shared", "a-only", "b-only"])],
            null,
            (providerA, ["shared", "a-only"]),
            (providerB, ["shared", "b-only"]));
        var clock = new ManualClock();
        var engine = new RouterEngine(clock);

        Assert.True(router.TryResolve("rr", out var first, engine, "chat-1", "turn-1"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(router.TryResolve("rr", out var secondTurn, engine, "chat-1", "turn-2"));
        Assert.True(router.TryResolve("rr", out var otherChat, engine, "chat-2", "turn-1"));

        // The warm conversation keeps its provider and model, so its prompt cache is reused.
        Assert.Equal("provider-a", first.Provider.Id);
        Assert.Equal(first, secondTurn);
        // A new conversation goes to the next provider, so load still spreads.
        Assert.Equal("provider-b", otherChat.Provider.Id);
    }

    [Fact]
    public void RoundRobinMovesAConversationOnceItsCacheHasExpired()
    {
        var providerA = Provider("provider-a", 0);
        var providerB = Provider("provider-b", 1);
        var router = Router([RouterRule.Create("rr", RouterStrategies.RoundRobin, ["shared"])],
            null, (providerA, ["shared"]), (providerB, ["shared"]));
        var clock = new ManualClock();
        var engine = new RouterEngine(clock);

        Assert.True(router.TryResolve("rr", out var first, engine, "chat", "turn-1"));
        clock.Advance(RouterEngine.CacheWarmth + TimeSpan.FromSeconds(1));
        Assert.True(router.TryResolve("rr", out var afterIdle, engine, "chat", "turn-2"));

        Assert.NotEqual(first.Provider.Id, afterIdle.Provider.Id);
    }

    [Fact]
    public void PremiumDoesNotBounceBackWhileTheFallbackCacheIsWarm()
    {
        var router = Router([RouterRule.Create("best", RouterStrategies.Premium, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));
        var clock = new ManualClock();
        var engine = new RouterEngine(clock);

        Assert.True(router.TryResolve("best", out var top, engine, "chat"));
        engine.MarkFailed(top.Provider, top.Model);
        Assert.True(router.TryResolve("best", out var fallback, engine, "chat"));
        Assert.NotEqual(top.Model, fallback.Model);

        // The failed model's cooldown ends, but the conversation's cache is on the fallback.
        clock.Advance(RouterEngine.Cooldown + TimeSpan.FromSeconds(1));
        Assert.True(router.TryResolve("best", out var stillWarm, engine, "chat"));
        Assert.Equal(fallback.Model, stillWarm.Model);

        // Idle past the cache lifetime: nothing left to keep, so the top choice returns.
        clock.Advance(RouterEngine.CacheWarmth + TimeSpan.FromSeconds(1));
        Assert.True(router.TryResolve("best", out var afterIdle, engine, "chat"));
        Assert.Equal(top.Model, afterIdle.Model);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void StickyKeepsASessionOnOneModel()
    {
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("stay", RouterStrategies.Sticky, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("stay", out var first, engine, "session-a"));
        for (var i = 0; i < 5; i++)
        {
            Assert.True(router.TryResolve("stay", out var again, engine, "session-a"));
            Assert.Equal(first.Model, again.Model);
        }
    }

    [Fact]
    public void StickyMovesOnWhenItsModelStopsAnswering()
    {
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("stay", RouterStrategies.Sticky, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("stay", out var first, engine, "session-a"));

        // The model that was answering starts failing, so the session must move rather
        // than keep asking something that is not there.
        engine.MarkFailed(first.Provider, first.Model);

        Assert.True(router.TryResolve("stay", out var second, engine, "session-a"));
        Assert.NotEqual(first.Model, second.Model);

        // And the new choice is what stays put.
        Assert.True(router.TryResolve("stay", out var third, engine, "session-a"));
        Assert.Equal(second.Model, third.Model);
    }

    [Fact]
    public void TwoSessionsCanSitOnDifferentModels()
    {
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("stay", RouterStrategies.Sticky, ["kimi-k3", "gpt-5.6-luna", "claude-opus-4-5"])],
            null, Zen("kimi-k3", "gpt-5.6-luna", "claude-opus-4-5"));

        Assert.True(router.TryResolve("stay", out var a, engine, "session-a"));
        Assert.True(router.TryResolve("stay", out var b, engine, "session-b"));

        // Each session remembers its own, so a second conversation is not dragged onto
        // the first one's model.
        Assert.True(router.TryResolve("stay", out var a2, engine, "session-a"));
        Assert.True(router.TryResolve("stay", out var b2, engine, "session-b"));
        Assert.Equal(a.Model, a2.Model);
        Assert.Equal(b.Model, b2.Model);
    }

    [Fact]
    public void ACoolingModelStepsAsideForAnotherInThePool()
    {
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("best", RouterStrategies.Premium, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("best", out var first, engine, "s1"));
        Assert.Equal("kimi-k3", first.Model);

        engine.MarkFailed(first.Provider, first.Model);

        Assert.True(router.TryResolve("best", out var second, engine, "s1"));
        Assert.Equal("gpt-5.6-luna", second.Model);
    }

    [Fact]
    public void APoolWithNothingAvailableStillAnswersRatherThanRefusing()
    {
        // A stale cooldown must not take a name offline: every model in the pool being
        // marked failed is a reason to try again, not to give up.
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("only", RouterStrategies.Premium, ["kimi-k3"])],
            null, Zen("kimi-k3"));

        Assert.True(router.TryResolve("only", out var first, engine, "s1"));
        engine.MarkFailed(first.Provider, first.Model);

        Assert.True(router.TryResolve("only", out var again, engine, "s1"));
        Assert.Equal("kimi-k3", again.Model);
    }

    [Fact]
    public void ARecoveredModelComesBackIntoRotation()
    {
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("best", RouterStrategies.Premium, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("best", out var first, engine, "s1"));
        engine.MarkFailed(first.Provider, first.Model);
        engine.MarkSucceeded(first.Provider, first.Model);

        Assert.True(router.TryResolve("best", out var again, engine, "s1"));
        Assert.Equal("kimi-k3", again.Model);   // dearest again, not stuck on the fallback
    }

    [Fact]
    public void AnUnpricedModelSortsLastInBothDirections()
    {
        // Unknown is not evidence of quality and not evidence of being cheap, so it is
        // never promoted to the front of either ordering.
        var router = Router(
            [RouterRule.Create("q", RouterStrategies.Premium, ["mystery", "gpt-5.6-luna"]),
             RouterRule.Create("c", RouterStrategies.FreeFirst, ["mystery", "gpt-5.6-luna"])],
            null, (Provider("private"), ["mystery", "gpt-5.6-luna"]));

        Assert.True(router.TryResolve("q", out var dearest, new RouterEngine(), "s1"));
        Assert.Equal("gpt-5.6-luna", dearest.Model);

        Assert.True(router.TryResolve("c", out var cheapest, new RouterEngine(), "s1"));
        Assert.Equal("gpt-5.6-luna", cheapest.Model);
    }

    [Fact]
    public void ARuleWithNoModelsSelectedIsNotUsable()
    {
        Assert.False(RouterRule.Create("empty", RouterStrategies.Sticky).IsUsable);
        Assert.False(RouterRule.Create("empty", RouterStrategies.Sticky, []).IsUsable);
        Assert.True(RouterRule.Create("ok", RouterStrategies.Sticky, ["m"]).IsUsable);
        // And an unknown strategy is ignored rather than guessed at.
        Assert.False(RouterRule.Create("x", "nonsense", ["m"]).IsUsable);
    }

    [Fact]
    public void ARouterNameIsNeverForwardedAsAModelId()
    {
        // Everything in the pool is switched off, so the name cannot resolve. It must not
        // fall through to the sole-provider alias path, which would send "free" upstream.
        var zen = Provider("zen") with { DisabledModels = ["kimi-k3"] };
        var router = Router([RouterRule.Create("free", RouterStrategies.Sticky, ["kimi-k3"])], null, (zen, ["kimi-k3"]));

        Assert.False(router.TryResolve("free", out _, new RouterEngine(), "s1"));
        // A genuine alias still falls back, so that behaviour is intact.
        Assert.True(router.TryResolve("some-alias", out var alias, new RouterEngine(), "s1"));
        Assert.Equal("some-alias", alias.Model);
    }

    [Fact]
    public void ADisabledRuleIsNeitherAdvertisedNorResolvable()
    {
        var router = Router([RouterRule.Create("free", RouterStrategies.Sticky, ["kimi-k3"], enabled: false)],
            null, Zen("kimi-k3"));

        Assert.False(router.TryResolve("free", out _, new RouterEngine(), "s1"));
        Assert.DoesNotContain("free", router.AdvertisedModelNames);
    }

    [Fact]
    public void RoutersAreListedAlongsideModelsSoAnAgentCanSelectThem()
    {
        var router = Router([RouterRule.Create("mine", RouterStrategies.Sticky, ["kimi-k3"])], null, Zen("kimi-k3"));

        Assert.Equal(["kimi-k3", "mine"], router.AdvertisedModelNames);
        var json = System.Text.Json.JsonSerializer.Serialize(router.UnionModelsDocument());
        Assert.Contains("\"id\":\"mine\"", json);
        Assert.Contains("\"owned_by\":\"Router\"", json);
    }

    [Fact]
    public void ThePoolListTicksWhatTheRuleAlreadyHasAndKeepsWhatVanished()
    {
        var available = new List<RouterEditorForm.RouterPoolEntry>
        {
            new("kimi-k3", "OpenCode Zen", "$3 in / $15 out", ""),
            new("gpt-5.6-luna", "OpenCode Go", "$0.20 in / $1.20 out", ""),
        };

        // A new rule starts with nothing selected, so the default is not an accidental pool.
        var fresh = RouterEditorForm.BuildPool(null, available);
        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, row => Assert.False(row.Ticked));

        // An existing rule shows its own models ticked - and only those.
        var existing = RouterRule.Create("mine", RouterStrategies.Sticky, ["gpt-5.6-luna"]);
        var rows = RouterEditorForm.BuildPool(existing, available);
        Assert.True(rows.Single(r => r.Entry.Model == "gpt-5.6-luna").Ticked);
        Assert.False(rows.Single(r => r.Entry.Model == "kimi-k3").Ticked);

        // A model the rule names but the catalog no longer serves still appears, ticked.
        // Dropping it would mean editing a rule silently deleted an entry from it.
        var stale = RouterRule.Create("mine", RouterStrategies.Sticky, ["gone-model", "kimi-k3"]);
        var withStale = RouterEditorForm.BuildPool(stale, available);
        Assert.Equal(3, withStale.Count);
        var vanished = withStale.Single(r => r.Entry.Model == "gone-model");
        Assert.True(vanished.Ticked);
        Assert.Equal("not served now", vanished.Entry.ProviderName);
    }

    [Fact]
    public void FreeFirstPrefersAFreeModelOverACheaperPaidOne()
    {
        // Free is a fact, not a ranking: the free model leads even though a paid model in
        // the pool is nominally cheap. Only when it stops working does the rest get used.
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("f", RouterStrategies.FreeFirst, ["gpt-5.6-luna", "space-bunny-free"])],
            null, Zen("gpt-5.6-luna", "space-bunny-free"));

        Assert.True(router.TryResolve("f", out var first, engine, "s1"));
        Assert.Equal("space-bunny-free", first.Model);

        engine.MarkFailed(first.Provider, first.Model);
        Assert.True(router.TryResolve("f", out var second, engine, "s1"));
        Assert.Equal("gpt-5.6-luna", second.Model);
    }

    [Fact]
    public void FreeFirstWithNoFreeModelInThePoolFallsBackToTheCheapest()
    {
        var router = Router([RouterRule.Create("f", RouterStrategies.FreeFirst, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("f", out var route, new RouterEngine(), "s1"));
        Assert.Equal("gpt-5.6-luna", route.Model);
    }

    [Fact]
    public void CodingPrefersAModelWhoseNameSaysSo()
    {
        // A name is all this is - no database carries a "good at code" field - so a model
        // the provider named for code leads the pool.
        var router = Router([RouterRule.Create("c", RouterStrategies.Coding, ["gpt-5.6-luna", "kimi-k2.7-code"])],
            null, Zen("gpt-5.6-luna", "kimi-k2.7-code"));

        Assert.True(router.TryResolve("c", out var route, new RouterEngine(), "s1"));
        Assert.Equal("kimi-k2.7-code", route.Model);
    }

    [Fact]
    public void CodingWithNothingNamedForCodeStillAnswersFromThePool()
    {
        // A pool of general models must not be refused just because none of them says
        // "code" in its id.
        var router = Router([RouterRule.Create("c", RouterStrategies.Coding, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("c", out var route, new RouterEngine(), "s1"));
        Assert.Equal("kimi-k3", route.Model);   // falls back to dearest first
    }

    [Fact]
    public void ThePickerOffersOnlyModelsTheOperatorIsActuallyServing()
    {
        // A model switched off on the Models tab must not be offered as something to put
        // in a router's pool: the pool is a subset of what is served, never a way around
        // the switch.
        var zen = Provider("zen") with { DisabledModels = ["kimi-k3"] };
        var router = Router([], null, (zen, ["kimi-k3", "gpt-5.6-luna"]));

        var offered = router.RoutesForPicker.Select(r => r.Model).ToArray();

        Assert.Contains("gpt-5.6-luna", offered);
        Assert.DoesNotContain("kimi-k3", offered);
        // And a disabled provider offers nothing at all.
        var off = new ProviderSettings("off", "off", ProviderKinds.OpenAi, "https://off.example/v1", "k", false, 1);
        var withOff = Router([], null, (off, ["gemma3"]));
        Assert.Empty(withOff.RoutesForPicker);
    }

    [Fact]
    public void AConfigWrittenWithAnOldStrategyNameStillWorks()
    {
        // A config written when the strategy was called something else must keep working
        // rather than quietly becoming an unusable rule.
        Assert.Equal(RouterStrategies.FreeFirst, RouterStrategies.Canonical("cheapest-first"));
        Assert.Equal(RouterStrategies.Premium, RouterStrategies.Canonical("quality-first"));
        Assert.Equal(RouterStrategies.Sticky, RouterStrategies.Canonical("STICKY"));
        Assert.False(RouterStrategies.IsKnown("nonsense"));

        var router = Router([RouterRule.Create("old", "quality-first", ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        Assert.True(router.TryResolve("old", out var route, new RouterEngine(), "s1"));
        Assert.Equal("kimi-k3", route.Model);
    }

    [Fact]
    public void AConversationWithoutASessionHeaderStillGetsItsOwnStickyChoice()
    {
        // No session header, so the opening message decides. A new chat is a new key, which
        // is what makes "sticky" mean per conversation rather than per client forever.
        var engine = new RouterEngine();
        var router = Router([RouterRule.Create("stay", RouterStrategies.Sticky, ["kimi-k3", "gpt-5.6-luna"])],
            null, Zen("kimi-k3", "gpt-5.6-luna"));

        var chatOne = RelaySession.Key(_ => null, Body("first question"), "10.0.0.5");
        var chatOneLater = RelaySession.Key(_ => null, Body("first question", "and a follow-up"), "10.0.0.5");
        var chatTwo = RelaySession.Key(_ => null, Body("something else entirely"), "10.0.0.5");

        Assert.Equal(chatOne, chatOneLater);          // same conversation, later turn
        Assert.NotEqual(chatOne, chatTwo);            // a new chat

        Assert.True(router.TryResolve("stay", out var a, engine, chatOne));
        Assert.True(router.TryResolve("stay", out var a2, engine, chatOneLater));
        Assert.Equal(a.Model, a2.Model);
    }

    [Fact]
    public void ASessionHeaderOutranksTheMessageFingerprint()
    {
        var viaHeader = RelaySession.Key(name => name == "x-relay-session-id" ? "abc" : null, Body("hello"), "10.0.0.5");
        Assert.Equal("x-relay-session-id:abc", viaHeader);

        // And a native agent header is accepted when the relay's own is absent.
        var native = RelaySession.Key(name => name == "x-opencode-session" ? "xyz" : null, Body("hello"), "10.0.0.5");
        Assert.Equal("x-opencode-session:xyz", native);
    }

    [Fact]
    public void TheFingerprintKeysOnTheOpeningTurnInEitherDialect()
    {
        var openAi = """
        {"model":"m","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"the opening line"}]}
        """;
        var anthropic = """
        {"model":"m","system":"be brief","messages":[{"role":"user","content":[{"type":"text","text":"the opening line"}]}]}
        """;

        // Both dialects describe the same conversation, and the system turn is not part of
        // what identifies it.
        Assert.Equal(
            RelaySession.ConversationFingerprint(System.Text.Encoding.UTF8.GetBytes(openAi)),
            RelaySession.ConversationFingerprint(System.Text.Encoding.UTF8.GetBytes(anthropic)));

        // Nothing to key on is handled rather than throwing.
        Assert.Equal("none", RelaySession.ConversationFingerprint(null));
        Assert.Equal("none", RelaySession.ConversationFingerprint("not json"u8.ToArray()));
    }

    [Fact]
    public void AnthropicToolResultsStayInsideTheCurrentUserTurn()
    {
        const string firstTurn = """{"messages":[{"role":"user","content":[{"type":"text","text":"inspect the file"}]}]}""";
        const string toolContinuation = """{"messages":[{"role":"user","content":[{"type":"text","text":"inspect the file"}]},{"role":"assistant","content":[{"type":"tool_use","id":"read-1","name":"read","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"read-1","content":"file contents"}]}]}""";
        const string nextTurn = """{"messages":[{"role":"user","content":[{"type":"text","text":"inspect the file"}]},{"role":"assistant","content":[{"type":"tool_use","id":"read-1","name":"read","input":{}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"read-1","content":"file contents"}]},{"role":"assistant","content":[{"type":"text","text":"Here is the summary."}]},{"role":"user","content":[{"type":"text","text":"now edit it"}]}]}""";

        Assert.Equal(RelaySession.TurnKey(System.Text.Encoding.UTF8.GetBytes(firstTurn)),
            RelaySession.TurnKey(System.Text.Encoding.UTF8.GetBytes(toolContinuation)));
        Assert.NotEqual(RelaySession.TurnKey(System.Text.Encoding.UTF8.GetBytes(toolContinuation)),
            RelaySession.TurnKey(System.Text.Encoding.UTF8.GetBytes(nextTurn)));
    }

    private static byte[] Body(params string[] userTurns)
    {
        var messages = string.Join(",", userTurns.Select(t => $$"""{"role":"user","content":"{{t}}"}"""));
        return System.Text.Encoding.UTF8.GetBytes($$"""{"model":"m","messages":[{{messages}}]}""");
    }

    [Fact]
    public void ConfigWithoutRulesStillLoadsAndKeepsItsKey()
    {
        var v3 = """
        {
          "SchemaVersion": 3,
          "LocalApiKey": "local-kept-key",
          "Providers": [
            { "Id": "p", "Name": "P", "Kind": "openai", "BaseUrl": "https://p.example", "Enabled": true, "Priority": 0 }
          ]
        }
        """;

        var config = RelayConfigReader.FromJson(System.Text.Encoding.UTF8.GetBytes(v3));

        Assert.NotNull(config);
        Assert.Equal(RelayConfig.CurrentSchemaVersion, config!.SchemaVersion);
        Assert.Equal("local-kept-key", config.LocalApiKey);
        Assert.Single(config.Providers);
        Assert.Empty(config.UsableRules);
    }

    [Fact]
    public void RulesRoundTripWithTheirPoolsAndDuplicatesAreDropped()
    {
        var json = """
        {
          "SchemaVersion": 5,
          "LocalApiKey": "local-kept-key",
          "Providers": [],
          "RouterRules": [
            { "Name": "free", "Strategy": "sticky", "Models": ["space-bunny-free", "big-pickle"], "Enabled": true },
            { "Name": "FREE", "Strategy": "round-robin", "Models": ["x"], "Enabled": true },
            { "Name": "empty", "Strategy": "sticky", "Models": [], "Enabled": true },
            { "Name": "broken", "Strategy": "nonsense", "Models": ["y"], "Enabled": true },
            { "Name": "dupes", "Strategy": "sticky", "Models": ["a", "a", "A", "b"], "Enabled": true }
          ]
        }
        """;

        var config = RelayConfigReader.FromJson(System.Text.Encoding.UTF8.GetBytes(json))!;

        // The duplicate name collapses, the unusable strategy is dropped, and pools are
        // de-duplicated case-insensitively so rotation cannot land on one model twice.
        Assert.Equal(3, config.RouterRules!.Count);
        Assert.Equal("free", config.RouterRules![0].Name);
        Assert.Equal(RouterStrategies.Sticky, config.RouterRules[0].Strategy);
        Assert.Equal(2, config.RouterRules[0].Pool.Count);
        Assert.Equal(2, config.RouterRules[2].Pool.Count);
        // "empty" has no models, so it is stored but not usable.
        Assert.Equal(2, config.UsableRules.Count);
        Assert.DoesNotContain(config.UsableRules, r => r.Name == "empty");
    }
}
