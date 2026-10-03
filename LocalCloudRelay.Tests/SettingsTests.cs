using System.Text;
using System.Text.Json;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class SettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"claude-relay-{Guid.NewGuid():N}.dat");

    [Fact]
    public void ConfigRoundTripIsEncryptedAndPreservesValues()
    {
        var path = TempFile();
        try
        {
            var store = new RelaySettingsStore(path);
            var expected = new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-stable-key",
            [
                new ProviderSettings("corp", "Corp gateway", ProviderKinds.OpenAi, "https://gw.example", "upstream-secret", true, 0),
                new ProviderSettings("ollama", "Local Ollama", ProviderKinds.Local, "http://127.0.0.1:11434/v1", null, true, 1)
            ]);
            store.Save(expected);

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("local-stable-key", loaded!.LocalApiKey);
            Assert.Equal(2, loaded.Providers.Count);
            Assert.Equal("Corp gateway", loaded.Providers[0].Name);
            Assert.Equal("upstream-secret", loaded.Providers[0].ApiKey);
            Assert.Equal(ProviderKinds.Local, loaded.Providers[1].Kind);
            Assert.Null(loaded.Providers[1].ApiKey);

            var raw = File.ReadAllBytes(path);
            Assert.NotEqual(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expected)), raw);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void MigratesLegacySingleGatewayBlobToSchemaV2()
    {
        // The pre-provider on-disk shape. It must keep working without a migration step.
        var legacy = """
        {"UpstreamUrl":"https://old-gw.example/","UpstreamApiKey":"legacy-secret","LocalApiKey":"local-legacy"}
        """;

        var config = RelayConfigReader.FromJson(Encoding.UTF8.GetBytes(legacy));

        Assert.NotNull(config);
        Assert.Equal(RelayConfig.CurrentSchemaVersion, config!.SchemaVersion);
        Assert.Equal("local-legacy", config.LocalApiKey);
        var provider = Assert.Single(config.Providers);
        Assert.Equal("https://old-gw.example", provider.BaseUrl);
        Assert.Equal("legacy-secret", provider.ApiKey);
        Assert.True(provider.Enabled);
    }

    [Fact]
    public void LegacyBlobSurvivesProtectionRoundTrip()
    {
        var path = TempFile();
        try
        {
            var legacy = """
            {"UpstreamUrl":"https://old-gw.example/","UpstreamApiKey":"legacy-secret","LocalApiKey":"local-legacy"}
            """;
            var bytes = System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(legacy), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, bytes);

            var loaded = new RelaySettingsStore(path).Load();

            Assert.NotNull(loaded);
            Assert.Equal("local-legacy", loaded!.LocalApiKey);
            Assert.Single(loaded.Providers);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void V2ConfigIsReadBackWithAllProviders()
    {
        var json = """
        {"SchemaVersion":2,"LocalApiKey":"local-x","Providers":[
          {"Id":"a","Name":"A","Kind":"openai","BaseUrl":"https://a.example","ApiKey":"ka","Enabled":true,"Priority":0},
          {"Id":"b","Name":"B","Kind":"anthropic","BaseUrl":"https://b.example/","ApiKey":null,"Enabled":false,"Priority":1}
        ]}
        """;

        var config = RelayConfigReader.FromJson(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(config);
        Assert.Equal(2, config!.Providers.Count);
        Assert.Equal("https://b.example", config.Providers[1].BaseUrl); // trailing slash trimmed
        Assert.False(config.Providers[1].Enabled);
        // Only enabled providers route.
        Assert.Single(config.EnabledProviders);
    }

    [Fact]
    public void LegacyProviderRecordsDefaultToApiKeyAuthentication()
    {
        var json = """{"SchemaVersion":2,"LocalApiKey":"local-k","Providers":[{"Id":"p","Name":"P","Kind":"openai","BaseUrl":"https://p.example","ApiKey":"key","Enabled":true,"Priority":0}]}""";

        var config = RelayConfigReader.FromJson(Encoding.UTF8.GetBytes(json));

        Assert.Equal(ProviderAuthMode.ApiKey, Assert.Single(config!.Providers).AuthMode);
        Assert.Null(config.OAuthHostId);
    }

    [Fact]
    public void ProviderAccountMetadataAndOAuthCredentialsRoundTrip()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var expected = new ProviderSettings("account-1", "Personal OpenAI", ProviderKinds.OpenAi,
            "https://api.example", null, true, 0)
        {
            AuthMode = ProviderAuthMode.OAuth,
            AccountId = "chatgpt-account",
            ProjectId = "project-1",
            CliExecutable = "agy.exe",
            ImportedModels = ["gpt-5.6", "gpt-5.6-mini"],
            ModelsFetchedAt = expiry.AddMinutes(-2),
            OAuthTokens = new ProviderOAuthTokens("access-token", "refresh-token", expiry,
                "openid model.read", "Bearer", "identity-token"),
            OAuthClientId = "desktop-client-id",
            OAuthClientSecret = "desktop-client-secret",
            OAuthEmail = "user@example.com",
            OAuthIssuer = OpenAiChatGptOAuth.Issuer
        };

        var serialized = JsonSerializer.SerializeToUtf8Bytes(new RelayConfig(
            RelayConfig.CurrentSchemaVersion, "local-key", [expected], OAuthHostId: "installation-host-id"));
        var config = RelayConfigReader.FromJson(serialized)!;
        var loaded = Assert.Single(config.Providers);

        Assert.Equal(expected.AuthMode, loaded.AuthMode);
        Assert.Equal(expected.AccountId, loaded.AccountId);
        Assert.Equal(expected.ProjectId, loaded.ProjectId);
        Assert.Equal(expected.CliExecutable, loaded.CliExecutable);
        Assert.Equal(expected.ImportedModels, loaded.ImportedModels);
        Assert.Equal(expected.ModelsFetchedAt, loaded.ModelsFetchedAt);
        Assert.Equal(expected.OAuthTokens, loaded.OAuthTokens);
        Assert.Equal(expected.OAuthClientId, loaded.OAuthClientId);
        Assert.Equal(expected.OAuthClientSecret, loaded.OAuthClientSecret);
        Assert.Equal(expected.OAuthEmail, loaded.OAuthEmail);
        Assert.Equal(expected.OAuthIssuer, loaded.OAuthIssuer);
        Assert.Equal("installation-host-id", config.OAuthHostId);

        var path = TempFile();
        try
        {
            var store = new RelaySettingsStore(path);
            store.Save(new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key", [expected]));
            Assert.DoesNotContain("desktop-client-secret", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.Equal(expected.OAuthClientSecret, Assert.Single(store.Load()!.Providers).OAuthClientSecret);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SettingsStoreAtomicallySavesOpenAiRegistrationIdentityAndRotatedTokens()
    {
        var path = TempFile();
        try
        {
            var profile = new ProviderSettings("p", "OpenAI", ProviderKinds.OpenAi,
                "https://api.openai.com/v1", null, true, 0, AuthMode: ProviderAuthMode.OAuth);
            var store = new RelaySettingsStore(path);
            store.Save(new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key", [profile]));
            store.SaveOpenAiRegistration("p", "issued-client-id");
            var tokens = new OAuthTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
                "openid resource.invoke chatgpt.tokens.use.direct", "Bearer", "identity-token");
            store.SaveOpenAiIdentity("p", "issued-client-id",
                new OpenAiAccountIdentity("verified-subject", "user@example.com", OpenAiChatGptOAuth.Issuer), tokens);
            var loaded = store.FindProvider("p")!;
            var retrieved = await store.GetAsync("p");

            Assert.Equal("issued-client-id", loaded.OAuthClientId);
            Assert.Equal("verified-subject", loaded.AccountId);
            Assert.Equal("user@example.com", loaded.OAuthEmail);
            Assert.Equal("access", retrieved!.AccessToken);
            Assert.Equal("identity-token", retrieved.IdToken);
            Assert.DoesNotContain("identity-token", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task SettingsStoreSavesGeminiDesktopCredentialsAndCloudProjectInsideProtectedSettings()
    {
        var path = TempFile();
        try
        {
            var profile = new ProviderSettings("gemini", "Personal Gemini", ProviderKinds.Gemini,
                "https://generativelanguage.googleapis.com", null, true, 0);
            var store = new RelaySettingsStore(path);
            store.Save(new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key", [profile]));
            store.SaveGeminiCredentials("gemini", "desktop-client-id", "desktop-client-secret", "cloud-project",
                new OAuthTokenSet("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1),
                    "Bearer", GeminiOAuth.CloudPlatformScope + " " + GeminiOAuth.GenerativeLanguageScope));

            var loaded = store.FindProvider("gemini")!;
            var tokens = await store.GetAsync("gemini");

            Assert.Equal(ProviderAuthMode.OAuth, loaded.AuthMode);
            Assert.Equal("desktop-client-id", loaded.OAuthClientId);
            Assert.Equal("desktop-client-secret", loaded.OAuthClientSecret);
            Assert.Equal("cloud-project", loaded.ProjectId);
            Assert.Equal(GeminiOAuth.Issuer, loaded.OAuthIssuer);
            Assert.Equal("access-token", tokens!.AccessToken);
            var protectedFile = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("desktop-client-secret", protectedFile);
            Assert.DoesNotContain("refresh-token", protectedFile);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task ConcurrentOAuthHostIdCreationReusesOneInstallationIdentity()
    {
        var path = TempFile();
        try
        {
            var first = new RelaySettingsStore(path);
            var second = new RelaySettingsStore(path);
            first.Save(new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key", []));

            var ids = await Task.WhenAll(
                Task.Run(first.GetOrCreateOAuthHostId),
                Task.Run(second.GetOrCreateOAuthHostId));

            Assert.Equal(ids[0], ids[1]);
            Assert.Equal(ids[0], first.Load()!.OAuthHostId);
            Assert.NotEqual("profile-1", ids[0]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ProviderCredentialDisplayStringsAreRedacted()
    {
        var tokens = new ProviderOAuthTokens("access-secret", "refresh-secret", null);
        var provider = new ProviderSettings("p", "P", ProviderKinds.OpenAi, "https://p.example",
            "api-key-secret", true, 0)
        {
            OAuthTokens = tokens,
            OAuthClientSecret = "client-secret"
        };

        Assert.DoesNotContain("access-secret", tokens.ToString());
        Assert.DoesNotContain("refresh-secret", tokens.ToString());
        Assert.DoesNotContain("api-key-secret", provider.ToString());
        Assert.DoesNotContain("access-secret", provider.ToString());
        Assert.DoesNotContain("refresh-secret", provider.ToString());
        Assert.DoesNotContain("client-secret", provider.ToString());
    }

    [Fact]
    public async Task ConcurrentSettingsUpdatesPreserveRotatedCredentialsAndUiEdits()
    {
        var path = TempFile();
        try
        {
            var initial = new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-key",
            [
                new ProviderSettings("p", "Before", ProviderKinds.OpenAi, "https://p.example", null, true, 0)
                {
                    AuthMode = ProviderAuthMode.OAuth,
                    OAuthTokens = new ProviderOAuthTokens("old-access", "old-refresh", null)
                }
            ]);
            var firstStore = new RelaySettingsStore(path);
            var secondStore = new RelaySettingsStore(path);
            firstStore.Save(initial);
            using var start = new ManualResetEventSlim();

            var rotate = Task.Run(() =>
            {
                start.Wait();
                firstStore.Update(current =>
                {
                    Thread.Sleep(30);
                    var provider = current!.Providers.Single();
                    return current with { Providers = [provider with { OAuthTokens = new ProviderOAuthTokens("new-access", "new-refresh", null) }] };
                });
            });
            var edit = Task.Run(() =>
            {
                start.Wait();
                secondStore.Update(current =>
                {
                    Thread.Sleep(30);
                    var provider = current!.Providers.Single();
                    return current with { Providers = [provider with { Name = "After" }] };
                });
            });

            start.Set();
            await Task.WhenAll(rotate, edit);
            var saved = Assert.Single(firstStore.Load()!.Providers);

            Assert.Equal("After", saved.Name);
            Assert.Equal("new-access", saved.OAuthTokens!.AccessToken);
            Assert.Equal("new-refresh", saved.OAuthTokens.RefreshToken);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void SchemaV2SurvivesTheUpgradeToV3WithItsKeyAndProvidersIntact()
    {
        // The key is the one thing a user cannot regenerate. If the v2 -> v3 upgrade
        // dropped it, Load would return null and every configured provider plus the
        // LAN key would be silently lost.
        var v2 = """
        {
          "SchemaVersion": 2,
          "LocalApiKey": "local-user-abc123",
          "Providers": [
            { "Id": "go", "Name": "OpenCode Go", "Kind": "openai", "BaseUrl": "https://opencode.ai/zen/go/v1", "ApiKey": "sk-go", "Enabled": true, "Priority": 0 },
            { "Id": "ollama", "Name": "Local Ollama", "Kind": "local", "BaseUrl": "http://127.0.0.1:11434/v1", "ApiKey": null, "Enabled": true, "Priority": 1 }
          ]
        }
        """;

        var config = RelayConfigReader.FromJson(Encoding.UTF8.GetBytes(v2));

        Assert.NotNull(config);
        Assert.Equal(RelayConfig.CurrentSchemaVersion, config!.SchemaVersion);
        Assert.Equal("local-user-abc123", config.LocalApiKey);
        Assert.Equal(2, config.Providers.Count);
        Assert.Equal("https://opencode.ai/zen/go/v1", config.Providers[0].BaseUrl);
        Assert.Equal("sk-go", config.Providers[0].ApiKey);
        // The local kind must survive, or it starts being charged token prices.
        Assert.Equal(ProviderKinds.Local, config.Providers[1].Kind);
        Assert.True(ProviderKinds.IsFree(config.Providers[1].Kind));
        // Absent DisabledModels means serve everything, which is the v2 behaviour.
        Assert.All(config.Providers, p => Assert.True(p.ServesModel("anything")));
    }

    [Fact]
    public void ApplyingTheSameServeDecisionTwiceChangesNothing()
    {
        // A DataGridView checkbox column delivers CellValueChanged for its own
        // population, and again on a sort or a rebuild. When that event was trusted,
        // every launch appended a fresh switch-off for every model, and the list grew
        // without bound. WithModel has to be idempotent, not merely correct once.
        var provider = new ProviderSettings("p", "P", ProviderKinds.OpenAi, "https://p.example", null, true, 0);

        var off = provider.WithModel("m", false);
        off = off.WithModel("m", false).WithModel("m", false);
        Assert.Single(off.DisabledModels!);
        Assert.False(off.ServesModel("m"));

        // Case variants are the same model.
        Assert.Single(off.WithModel("M", false).DisabledModels!);

        var on = off.WithModel("m", true);
        Assert.Empty(on.DisabledModels!);
        Assert.True(on.ServesModel("m"));
        Assert.Empty(on.WithModel("m", true).DisabledModels!);

        // And a list that already contains duplicates is repaired on the next touch,
        // so an existing config cannot stay inflated forever. Disabling "c" adds it
        // once; the duplicate "a"/"A" pair collapses either way.
        var dirty = provider with { DisabledModels = ["a", "a", "A", "b"] };
        Assert.Equal(["a", "b", "c"], dirty.WithModel("c", false).DisabledModels!.ToArray());
        Assert.Equal(["b"], dirty.WithModel("a", true).DisabledModels!.ToArray());
    }

    [Fact]
    public void DisabledModelsRoundTripAndAreRespected()
    {
        var v3 = """
        {
          "SchemaVersion": 3,
          "LocalApiKey": "local-k",
          "Providers": [
            { "Id": "p", "Name": "P", "Kind": "openai", "BaseUrl": "https://p.example", "Enabled": true, "Priority": 0,
              "DisabledModels": ["Banned-Model", "another-one"] }
          ]
        }
        """;

        var config = RelayConfigReader.FromJson(Encoding.UTF8.GetBytes(v3))!;
        var provider = config.Providers[0];

        Assert.False(provider.ServesModel("banned-model"));   // case-insensitive
        Assert.False(provider.ServesModel("another-one"));
        Assert.True(provider.ServesModel("allowed-model"));

        // Toggling back on clears the entry rather than accumulating it.
        var restored = provider.WithModel("banned-model", true).WithModel("ANOTHER-ONE", true);
        Assert.Empty(restored.DisabledModels!);
        Assert.True(restored.ServesModel("banned-model"));
    }

    [Fact]
    public void ADisabledModelIsNeitherListedNorRoutable()
    {
        var provider = new ProviderSettings("p", "P", ProviderKinds.OpenAi, "https://p.example", null, true, 0)
            .WithModel("banned", false);
        var catalog = new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
        {
            ["p"] = ["allowed", "banned"]
        });

        var router = new ProviderRouter([provider], catalog);

        Assert.True(router.TryResolve("allowed", out _));
        Assert.False(router.TryResolve("banned", out _));

        var json = JsonSerializer.Serialize(router.UnionModelsDocument());
        Assert.Contains("allowed", json);
        Assert.DoesNotContain("banned", json);
        Assert.Equal(1, router.ModelCount);
    }

    [Fact]
    public void ADisabledModelIsNotResurrectedByTheSoleProviderFallback()
    {
        // The fallback exists so Claude Code's claude-sonnet-4-5 resolves to
        // claude-sonnet-4-5-20251001. It must not override a model that was switched off.
        var provider = new ProviderSettings("solo", "Solo", ProviderKinds.OpenAi, "https://solo.example", null, true, 0)
            .WithModel("banned", false);
        var catalog = new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
        {
            ["solo"] = ["known", "banned"]
        });

        var router = new ProviderRouter([provider], catalog);

        Assert.True(router.TryResolve("known", out _));
        Assert.False(router.TryResolve("banned", out _));
        // An alias that does not exist at all still falls back, as before.
        Assert.True(router.TryResolve("some-alias", out _));
    }

    [Fact]
    public void TheDataFolderIsRenamedFromThePreviousAppName()
    {
        // The app was ClaudeLanRelay until v1.0.0. The DPAPI key in that folder cannot
        // be regenerated, so the rename has to move the file rather than leave it. This
        // exercises the real resolver against a temp LocalAppData.
        var root = Path.Combine(Path.GetTempPath(), "relay-paths-" + Guid.NewGuid().ToString("N")[..8]);
        var legacy = Path.Combine(root, "ClaudeLanRelay");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.dat"), "sentinel");

        var resolved = RelayPaths.ResolveIn(root);

        Assert.Equal(Path.Combine(root, "LocalCloudRelay"), resolved);
        Assert.Equal("sentinel", File.ReadAllText(Path.Combine(resolved, "settings.dat")));
        Assert.False(Directory.Exists(legacy));
        Directory.Delete(root, true);
    }

    [Fact]
    public void AnExistingNewDataFolderIsLeftAlone()
    {
        // This is the normal path after the first run, and it must not resurrect or
        // delete anything: a re-run of the rename would be a data loss bug.
        var root = Path.Combine(Path.GetTempPath(), "relay-paths-" + Guid.NewGuid().ToString("N")[..8]);
        var current = Path.Combine(root, "LocalCloudRelay");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "settings.dat"), "current");

        Assert.Equal(current, RelayPaths.ResolveIn(root));
        Assert.Equal("current", File.ReadAllText(Path.Combine(current, "settings.dat")));
        Directory.Delete(root, true);
    }

    [Fact]
    public void AFreshInstallGetsTheNewFolderAndCreatesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "relay-paths-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        Assert.Equal(Path.Combine(root, "LocalCloudRelay"), RelayPaths.ResolveIn(root));
        // Resolution must not create anything; the store creates the directory on save.
        Assert.False(Directory.Exists(Path.Combine(root, "LocalCloudRelay")));
        Directory.Delete(root, true);
    }

    [Fact]
    public void SettingsRoundTripThroughTheResolverOnceMigrated()
    {
        // End to end over the rename: a config saved by the old name is readable after.
        var root = Path.Combine(Path.GetTempPath(), "relay-paths-" + Guid.NewGuid().ToString("N")[..8]);
        var legacy = Path.Combine(root, "ClaudeLanRelay");
        Directory.CreateDirectory(legacy);

        var before = new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-migrated-key",
            [new ProviderSettings("p", "P", ProviderKinds.OpenAi, "https://p.example", "sk-x", true, 0)]);
        new RelaySettingsStore(Path.Combine(legacy, "settings.dat")).Save(before);

        var after = new RelaySettingsStore(Path.Combine(RelayPaths.ResolveIn(root), "settings.dat")).Load();

        Assert.NotNull(after);
        Assert.Equal("local-migrated-key", after!.LocalApiKey);
        Directory.Delete(root, true);
    }

    [Fact]
    public void ConfigWithNoProvidersStillRoundTripsAndKeepsItsKey()
    {
        // The key is minted on first run before any provider exists. If that state did
        // not round-trip, every launch would mint a new key and invalidate the one
        // already handed to clients.
        var path = TempFile();
        try
        {
            var store = new RelaySettingsStore(path);
            var expected = new RelayConfig(RelayConfig.CurrentSchemaVersion, "local-first-run-key", []);
            store.Save(expected);

            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal("local-first-run-key", loaded!.LocalApiKey);
            Assert.Empty(loaded.Providers);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void AnEmptyProviderListIsNotMistakenForALegacyBlob()
    {
        var json = """{"SchemaVersion":2,"LocalApiKey":"local-k","Providers":[]}""";

        var config = RelayConfigReader.FromJson(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(config);
        Assert.Equal("local-k", config!.LocalApiKey);
        Assert.Empty(config.Providers);
        Assert.Empty(config.EnabledProviders);
    }

    [Fact]
    public void MissingLocalKeyIsRejectedRatherThanServingWithAnEmptyKey()
    {
        Assert.Null(RelayConfigReader.FromJson(Encoding.UTF8.GetBytes("""{"UpstreamUrl":"https://x.example"}""")));
        Assert.Null(RelayConfigReader.FromJson(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(RelayConfigReader.FromJson(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public void CorruptSettingsFallBackToFirstRun()
    {
        var path = TempFile();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Null(new RelaySettingsStore(path).Load());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void CorruptSettingsArePreservedWithBadSuffix()
    {
        var path = TempFile();
        var corruptData = new byte[] { 1, 2, 3, 4 };
        var dir = Path.GetDirectoryName(path)!;
        var filename = Path.GetFileName(path);
        var pattern = filename + ".bad-";

        try
        {
            File.WriteAllBytes(path, corruptData);

            var store = new RelaySettingsStore(path);
            var loaded = store.Load();

            // Load returns null and LastLoadError should be set
            Assert.Null(loaded);
            Assert.NotNull(store.LastLoadError);
            Assert.Contains(".bad-", store.LastLoadError);

            // Original corrupt file should be moved, not exist anymore
            Assert.False(File.Exists(path), "Original corrupt file should be moved");

            // Find the .bad-* file that was created
            var allFilesInDir = Directory.GetFiles(dir);
            var matchingFiles = allFilesInDir.Where(f =>
            {
                var fn = Path.GetFileName(f);
                return fn.StartsWith(pattern);
            }).ToList();
            var dirContents = string.Join("\n  ", allFilesInDir.Select(Path.GetFileName));
            Assert.True(matchingFiles.Count > 0, $"No .bad-* files found. Dir contents:\n  {dirContents}\nLooking for pattern: {pattern}");
            Assert.Single(matchingFiles);

            // Verify the corrupt data is preserved
            var preserved = File.ReadAllBytes(matchingFiles[0]);
            Assert.Equal(corruptData, preserved);

            // Clean up the .bad file
            File.Delete(matchingFiles[0]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try
            {
                var badFiles = Directory.GetFiles(dir).Where(f => Path.GetFileName(f).StartsWith(pattern)).ToList();
                foreach (var f in badFiles) File.Delete(f);
            }
            catch { }
        }
    }
}
