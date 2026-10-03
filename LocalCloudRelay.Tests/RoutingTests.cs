using System.Text;
using System.Text.Json;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class RoutingTests
{
    private static ProviderSettings Provider(string id, string name, int priority = 0, string kind = ProviderKinds.OpenAi) =>
        new(id, name, kind, $"https://{id}.example", "key-" + id, true, priority);

    private static ProviderRouter Router(params (ProviderSettings Provider, string[] Models)[] entries)
    {
        var catalog = new CatalogSnapshot(DateTimeOffset.UtcNow,
            entries.ToDictionary(e => e.Provider.Id, e => (IReadOnlyList<string>)e.Models, StringComparer.Ordinal));
        return new ProviderRouter(entries.Select(e => e.Provider), catalog);
    }

    [Fact]
    public void RoutesModelToTheProviderThatServesIt()
    {
        var router = Router(
            (Provider("corp", "Corp", 0), ["gpt-5.6-luna", "claude-sonnet-4-5"]),
            (Provider("ollama", "Local Ollama", 1), ["llama3.1-70b"]));

        Assert.True(router.TryResolve("llama3.1-70b", out var route));
        Assert.Equal("Local Ollama", route.Provider.Name);
        Assert.Equal("ollama", route.Provider.Id);

        Assert.True(router.TryResolve("gpt-5.6-luna", out var second));
        Assert.Equal("Corp", second.Provider.Name);
    }

    [Fact]
    public void AnExactMatchResolvesToTheModelNotTheProviderId()
    {
        // The route's Model is what the relay records as the served model, what it prices
        // an estimate against, and what it reports in telemetry. Returning the provider's
        // id here grouped every exact hit under an 8-hex key, so the per-model table and
        // the cost fallback both saw a model name that exists nowhere.
        var router = Router((Provider("corp", "Corp"), ["gpt-5.6-luna", "claude-sonnet-4-5"]));

        Assert.True(router.TryResolve("gpt-5.6-luna", out var route));

        Assert.Equal("gpt-5.6-luna", route.Model);
        Assert.NotEqual(route.Provider.Id, route.Model);
        // The invariant that was violated: no route may carry a provider id as its model.
        var ids = new[] { route.Provider.Id };
        Assert.All(router.AllRoutes, r => Assert.DoesNotContain(r.Model, ids));
    }

    [Fact]
    public void ResolvesModelAliasByPrefixMatch()
    {
        // Claude Code sends claude-sonnet-4-5; the gateway advertises the dated id.
        var router = Router((Provider("corp", "Corp"), ["claude-sonnet-4-5-20251001", "gpt-5.6-luna"]));

        Assert.True(router.TryResolve("claude-sonnet-4-5", out var route));
        Assert.Equal("claude-sonnet-4-5-20251001", route.Model);
    }

    [Fact]
    public void ResolvesDatedModelIdWhenGatewayListsBaseVersion()
    {
        // Claude Code sends dated id; gateway lists base version. With 2 providers this was failing.
        var router = Router(
            (Provider("corp", "Corp", 0), ["claude-sonnet-4-5"]),
            (Provider("ollama", "Ollama", 1), ["llama3.1-70b"]));

        Assert.True(router.TryResolve("claude-sonnet-4-5-20251001", out var route));
        Assert.Equal("claude-sonnet-4-5", route.Model);
        Assert.Equal("Corp", route.Provider.Name);
    }

    [Fact]
    public void FindsBaseNameWhenDatedVersionIsRequested()
    {
        // Client sends dated; only base exists in catalog.
        var router = Router(
            (Provider("corp", "Corp", 0), ["gpt-5.6-luna"]),
            (Provider("gw", "Gateway", 5), ["gpt-4-turbo"]));

        Assert.True(router.TryResolve("gpt-5.6-luna-20251001", out var route));
        Assert.Equal("gpt-5.6-luna", route.Model);
    }

    [Fact]
    public void TightensModelPrefixToAvoidSilentMismatches()
    {
        // gpt-4 should not match gpt-4.1 (dot-delimited). With 2 providers, sole-provider
        // fallback does not kick in.
        var router = Router(
            (Provider("corp", "Corp"), ["gpt-4.1", "gpt-5.5"]),
            (Provider("ollama", "Ollama"), ["llama3.1"]));

        Assert.False(router.TryResolve("gpt-4", out _));
    }

    [Fact]
    public void PrefersHigherPriorityProviderOnModelIdCollision()
    {
        var router = Router(
            (Provider("second", "Second choice", 5), ["shared-model"]),
            (Provider("first", "First choice", 0), ["shared-model"]));

        Assert.True(router.TryResolve("shared-model", out var route));
        Assert.Equal("First choice", route.Provider.Name);
    }

    [Fact]
    public void FallsBackToTheSoleEnabledProviderForAnUnknownModel()
    {
        var router = Router((Provider("solo", "Solo"), ["known-model"]));

        Assert.True(router.TryResolve("something-nobody-advertises", out var route));
        Assert.Equal("Solo", route.Provider.Name);
    }

    [Fact]
    public void AccountProfilesRouteOnlyImportedExactModels()
    {
        var account = Provider("claude-account", "Claude account") with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            AccountId = "claude-user",
            ImportedModels = ["claude-sonnet-4-5-20251001"]
        };
        var cachedCatalog = new CatalogSnapshot(DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [account.Id] = ["stale-catalog-model"]
            });
        var router = new ProviderRouter([account], cachedCatalog);

        Assert.True(router.TryResolve("claude-sonnet-4-5-20251001", out var route));
        Assert.Equal(account.Id, route.Provider.Id);
        Assert.Equal("claude-sonnet-4-5-20251001", route.Model);
        Assert.False(router.TryResolve("claude-sonnet-4-5", out _));
        Assert.False(router.TryResolve("CLAUDE-SONNET-4-5-20251001", out _));
        Assert.False(router.TryResolve("stale-catalog-model", out _));
        Assert.False(router.TryResolve("unknown-model", out _));
    }

    [Fact]
    public void AccountProfileModelsStayOwnedByTheirImportingProfile()
    {
        var claude = Provider("claude-account", "Claude account") with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            AccountId = "claude-user",
            ImportedModels = ["claude-model"]
        };
        var gemini = Provider("gemini-account", "Gemini account") with
        {
            AuthMode = ProviderAuthMode.OAuth,
            AccountId = "google-user",
            ImportedModels = ["gemini-model"]
        };
        var router = Router((claude, ["unrelated-cache-id"]), (gemini, ["another-stale-id"]));

        Assert.True(router.TryResolve("claude-model", out var claudeRoute));
        Assert.Equal(claude.Id, claudeRoute.Provider.Id);
        Assert.True(router.TryResolve("gemini-model", out var geminiRoute));
        Assert.Equal(gemini.Id, geminiRoute.Provider.Id);
        Assert.False(router.TryResolve("unknown-model", out _));
    }

    [Fact]
    public void DuplicateAccountModelsRequireProfileQualifiedNames()
    {
        var first = Provider("claude-one", "Claude one", 0) with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            ImportedModels = ["shared-model"]
        };
        var second = Provider("claude-two", "Claude two", 1) with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            ImportedModels = ["shared-model"]
        };
        var router = Router((first, []), (second, []));

        Assert.False(router.TryResolve("shared-model", out _));
        Assert.Contains("account:claude-one/shared-model", router.AdvertisedModelNames);
        Assert.Contains("account:claude-two/shared-model", router.AdvertisedModelNames);
        var json = JsonSerializer.Serialize(router.UnionModelsDocument());
        Assert.Contains("account:claude-one/shared-model", json);
        Assert.Contains("account:claude-two/shared-model", json);

        Assert.True(router.TryResolve("account:claude-one/shared-model", out var firstRoute));
        Assert.Equal(first.Id, firstRoute.Provider.Id);
        Assert.Equal("shared-model", firstRoute.Model);
        Assert.Equal("account:claude-one/shared-model", firstRoute.ViaAccountAlias);

        Assert.True(router.TryResolve("account:claude-two/shared-model", out var secondRoute));
        Assert.Equal(second.Id, secondRoute.Provider.Id);
        Assert.Equal("shared-model", secondRoute.Model);
        Assert.Equal("account:claude-two/shared-model", secondRoute.ViaAccountAlias);
    }

    [Fact]
    public void AccountAliasesCannotStealAnotherProfilesImportedModelId()
    {
        var first = Provider("claude-one", "Claude one", 0) with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            ImportedModels = ["shared-model"]
        };
        var second = Provider("claude-two", "Claude two", 1) with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            ImportedModels = ["shared-model"]
        };
        var realIdOwner = Provider("real-id-owner", "Real id owner", 2) with
        {
            AuthMode = ProviderAuthMode.OAuth,
            ImportedModels = ["account:claude-one/shared-model"]
        };
        var router = Router((first, []), (second, []), (realIdOwner, []));

        Assert.Contains("account:claude-one/shared-model", router.AdvertisedModelNames);
        Assert.Contains("account:claude-two/shared-model", router.AdvertisedModelNames);
        Assert.True(router.TryResolve("account:claude-one/shared-model", out var importedIdRoute));
        Assert.Equal(realIdOwner.Id, importedIdRoute.Provider.Id);
        Assert.Equal("account:claude-one/shared-model", importedIdRoute.Model);

        var firstAlias = router.AdvertisedModelNames.Single(name =>
            name.StartsWith("account:claude-one/shared-model", StringComparison.Ordinal) &&
            name != "account:claude-one/shared-model");
        Assert.True(router.TryResolve(firstAlias, out var firstRoute));
        Assert.Equal(first.Id, firstRoute.Provider.Id);
        Assert.Equal("shared-model", firstRoute.Model);
        Assert.Equal(firstAlias, firstRoute.ViaAccountAlias);

        Assert.True(router.TryResolve("account:claude-two/shared-model", out var secondRoute));
        Assert.Equal(second.Id, secondRoute.Provider.Id);
        Assert.Equal("shared-model", secondRoute.Model);
    }

    [Fact]
    public void CaseDistinctAccountModelsRemainSeparatelyAdvertisedAndResolvable()
    {
        var account = Provider("gemini-account", "Gemini account") with
        {
            AuthMode = ProviderAuthMode.OAuth,
            ImportedModels = ["Model-X", "model-x"]
        };
        var router = Router((account, []));

        Assert.Contains("Model-X", router.AdvertisedModelNames);
        Assert.Contains("model-x", router.AdvertisedModelNames);
        var json = JsonSerializer.Serialize(router.UnionModelsDocument());
        Assert.Contains("\"id\":\"Model-X\"", json);
        Assert.Contains("\"id\":\"model-x\"", json);
        Assert.True(router.TryResolve("Model-X", out var upperRoute));
        Assert.Equal("Model-X", upperRoute.Model);
        Assert.True(router.TryResolve("model-x", out var lowerRoute));
        Assert.Equal("model-x", lowerRoute.Model);

        foreach (var advertised in new[] { "Model-X", "model-x" })
        {
            Assert.True(router.TryResolve(advertised, out var route));
            Assert.Equal(account.Id, route.Provider.Id);
            Assert.Equal(advertised, route.Model);
        }
    }

    [Fact]
    public void ApiKeyProfileKeepsBareDuplicateWhileAccountGetsQualifiedAlias()
    {
        var account = Provider("claude-account", "Claude account", 0) with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            ImportedModels = ["shared-model"]
        };
        var apiKey = Provider("openai-key", "OpenAI key", 1);
        var router = Router((account, ["shared-model"]), (apiKey, ["shared-model"]));

        Assert.True(router.TryResolve("shared-model", out var bareRoute));
        Assert.Equal(apiKey.Id, bareRoute.Provider.Id);
        Assert.Contains("account:claude-account/shared-model", router.AdvertisedModelNames);
        Assert.True(router.TryResolve("account:claude-account/shared-model", out var qualifiedRoute));
        Assert.Equal(account.Id, qualifiedRoute.Provider.Id);
        Assert.Equal("shared-model", qualifiedRoute.Model);
        Assert.Equal("account:claude-account/shared-model", qualifiedRoute.ViaAccountAlias);
    }

    [Fact]
    public void GeneratedAccountAliasDoesNotShadowCaseVariantApiKeyModelId()
    {
        var account = Provider("claude-one", "Claude account", 0) with
        {
            AuthMode = ProviderAuthMode.CliAccount,
            ImportedModels = ["shared-model"]
        };
        var apiKeyWinner = Provider("api-upper", "API key winner", 1);
        var apiKeyVariant = Provider("api-lower", "API key variant", 2);
        var router = Router(
            (account, []),
            (apiKeyWinner, ["ACCOUNT:claude-one/shared-model", "shared-model"]),
            (apiKeyVariant, ["account:claude-one/shared-model", "shared-model"]));

        Assert.True(router.TryResolve("shared-model", out var bareRoute));
        Assert.Equal(apiKeyWinner.Id, bareRoute.Provider.Id);
        Assert.True(router.TryResolve("account:claude-one/shared-model", out var rawApiKeyRoute));
        Assert.Equal(apiKeyWinner.Id, rawApiKeyRoute.Provider.Id);
        Assert.Equal("account:claude-one/shared-model", rawApiKeyRoute.Model);

        Assert.Contains("account:claude-one/shared-model~2", router.AdvertisedModelNames);
        Assert.True(router.TryResolve("account:claude-one/shared-model~2", out var accountRoute));
        Assert.Equal(account.Id, accountRoute.Provider.Id);
        Assert.Equal("shared-model", accountRoute.Model);
        Assert.Equal("account:claude-one/shared-model~2", accountRoute.ViaAccountAlias);
    }

    [Fact]
    public void RejectsAnUnknownModelWhenSeveralProvidersCouldServeIt()
    {
        var router = Router(
            (Provider("a", "A"), ["model-a"]),
            (Provider("b", "B"), ["model-b"]));

        // Silently forwarding to a guess is what produced a confusing 502 before.
        Assert.False(router.TryResolve("model-c", out var route));
        Assert.Null(route);
    }

    [Fact]
    public void DisabledProvidersContributeNoModelsAndNoFallback()
    {
        var disabled = new ProviderSettings("off", "Off", ProviderKinds.OpenAi, "https://off.example", "k", false, 0);
        var catalog = new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
        {
            ["off"] = ["ghost-model"]
        });

        var router = new ProviderRouter([disabled], catalog);

        Assert.Empty(router.Providers);
        Assert.False(router.TryResolve("ghost-model", out _));
    }

    [Fact]
    public void UnionModelListIsDeduplicatedAndNamesItsProvider()
    {
        var router = Router(
            (Provider("corp", "Corp Gateway"), ["gpt-5.6-luna", "shared"]),
            (Provider("ollama", "Local Ollama"), ["llama3.1-70b", "shared"]));

        var json = JsonSerializer.Serialize(router.UnionModelsDocument());

        Assert.Contains("\"object\":\"list\"", json);
        Assert.Contains("gpt-5.6-luna", json);
        Assert.Contains("llama3.1-70b", json);
        // shared appears once, owned by the higher-priority provider.
        Assert.Equal(1, CountOccurrences(json, "\"id\":\"shared\""));
        Assert.Contains("\"owned_by\":\"Corp Gateway\"", json);
        Assert.Contains("\"owned_by\":\"Local Ollama\"", json);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

public sealed class ProviderProbeTests
{
    [Fact]
    public void ParsesOpenAiShapedModelList()
    {
        var ids = ProviderProbe.ParseModelIds("""{"object":"list","data":[{"id":"gpt-5.6-luna"},{"id":"gpt-4.1"}]}""");
        Assert.Equal(["gpt-4.1", "gpt-5.6-luna"], ids);
    }

    [Fact]
    public void ParsesGeminiShapedModelListAndStripsTheModelsPrefix()
    {
        var ids = ProviderProbe.ParseModelIds("""{"models":[{"name":"models/gemini-3-pro"},{"name":"models/gemini-3-flash"}]}""");
        Assert.Equal(["gemini-3-flash", "gemini-3-pro"], ids);
    }

    [Fact]
    public void ParsesOllamaShapedModelList()
    {
        var ids = ProviderProbe.ParseModelIds("""{"models":[{"name":"llama3.1:70b"},{"name":"qwen3:8b"}]}""");
        Assert.Equal(["llama3.1:70b", "qwen3:8b"], ids);
    }

    [Fact]
    public void ToleratesGarbageModelLists()
    {
        Assert.Empty(ProviderProbe.ParseModelIds("not json"));
        Assert.Empty(ProviderProbe.ParseModelIds("{}"));
        Assert.Empty(ProviderProbe.ParseModelIds(""));
        Assert.Empty(ProviderProbe.ParseModelIds("""{"data":"nope"}"""));
    }
}

public sealed class CatalogRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsDisabledRouterNamesReservedAfterRefresh(bool force)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"catalog-reserved-{Guid.NewGuid():N}.json");
        try
        {
            var provider = new ProviderSettings("gw", "Gateway", ProviderKinds.OpenAi, "https://gw.example", "key", true, 0);
            var config = new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key", [provider],
                [new RouterRule("free", RouterStrategies.RoundRobin, ["cached-model"], Enabled: false)]);
            var cache = new CatalogCache(cachePath);
            cache.Save(new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
            {
                ["gw"] = ["cached-model"]
            }));
            using var handler = new TestFailingHandler();
            using var client = new HttpClient(handler);

            var result = await CatalogRefreshRunner.RunAsync(config, cache, force, client);

            Assert.True(result.Router.IsDisabledRouter("free"));
            Assert.False(result.Router.TryResolve("free", out _));
            Assert.True(result.Router.TryResolve("cached-model", out _));
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task PreservesCachedModelsWhenAllProbesFail()
    {
        // When all provider probes fail, the cached models should be carried over
        // instead of saving an empty snapshot that wipes the model list.
        var tempDir = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create a config with one provider
            var provider = new ProviderSettings("gw", "Gateway", ProviderKinds.OpenAi, "https://gw.example", "key", true, 0);
            var config = new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key", [provider]);

            // Pre-populate cache with some models
            var cachePath = Path.Combine(tempDir, "catalog.json");
            var cache = new CatalogCache(cachePath);
            var cachedSnapshot = new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
            {
                ["gw"] = ["cached-model-1", "cached-model-2"]
            });
            cache.Save(cachedSnapshot);

            // Create an HTTP handler that returns 500 for all requests (simulating probe failure)
            var failingHandler = new TestFailingHandler();
            using var client = new HttpClient(failingHandler) { Timeout = TimeSpan.FromSeconds(5) };

            // Run catalog refresh with force=true to trigger probes
            var result = await CatalogRefreshRunner.RunAsync(config, cache, force: true, client);

            // The refresh should return the cached models despite all probes failing
            Assert.NotEmpty(result.Snapshot.ModelsByProviderId);
            Assert.True(result.Snapshot.ModelsByProviderId.ContainsKey("gw"));
            Assert.Equal(["cached-model-1", "cached-model-2"], result.Snapshot.ModelsByProviderId["gw"]);

            // The cache file should not have been overwritten (still contains cached models)
            var reloadedCache = cache.Load();
            Assert.NotEmpty(reloadedCache.ModelsByProviderId);
            Assert.Equal(["cached-model-1", "cached-model-2"], reloadedCache.ModelsByProviderId["gw"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class TestFailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Simulate provider returning 500 error
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}

public sealed class StreamCaptureTests
{
    [Fact]
    public void KeepsTheTailOfALongStreamSoTheUsageBlockSurvives()
    {
        // A head-truncating capture lost the final message_delta, which is exactly where
        // Anthropic reports usage - so the longest requests reported zero tokens and cost.
        using var inner = new MemoryStream();
        using var capture = new RelayBodyCaptureStream(inner, limit: 512);

        var filler = new string('x', 400);
        var written = 0;
        for (var i = 0; i < 20; i++)
        {
            var chunk = Encoding.UTF8.GetBytes($"data: {{\"delta\":\"{filler}\"}}\n\n");
            capture.Write(chunk);
            written += chunk.Length;
        }

        var usageChunk = Encoding.UTF8.GetBytes(
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":412}}\n\ndata: [DONE]\n\n");
        capture.Write(usageChunk);
        written += usageChunk.Length;

        var usage = RelayUsageParser.ParseServerSentEvents(capture.CapturedText);
        Assert.NotNull(usage);
        Assert.Equal(412, usage!.OutputTokens);

        // The client still receives every byte; only the capture is bounded.
        Assert.Equal(written, inner.Length);
        Assert.True(written > 4096, "test should actually exceed the capture limit");
        Assert.True(capture.CapturedText.Length <= 1024, "capture should stay bounded");
    }

    [Fact]
    public void SmallStreamIsCapturedWhole()
    {
        using var inner = new MemoryStream();
        using var capture = new RelayBodyCaptureStream(inner, limit: 4096);
        capture.Write(Encoding.UTF8.GetBytes("data: {\"usage\":{\"input_tokens\":9}}\n\n"));
        Assert.Equal("data: {\"usage\":{\"input_tokens\":9}}\n\n", capture.CapturedText);
    }
}
