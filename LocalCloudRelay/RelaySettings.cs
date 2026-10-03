using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;

namespace LocalCloudRelay;

public sealed class RelaySettingsStore : IOpenAiAccountStore, IGeminiAccountStore
{
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _path;
    private readonly object _pathLock;
    private string? _lastLoadError;

    public RelaySettingsStore(string? path = null)
    {
        _path = path ?? RelayPaths.File("settings.dat");
        _pathLock = PathLocks.GetOrAdd(System.IO.Path.GetFullPath(_path), static _ => new object());
    }

    public string Path => _path;
    public string? LastLoadError => _lastLoadError;

    public RelayConfig? Load()
    {
        lock (_pathLock) return LoadCore();
    }

    /// <summary>
    /// Applies a mutation to the latest DPAPI-protected configuration while holding a
    /// per-file lock across read, transform, and atomic write. Callers must express edits
    /// as changes to the value passed to the callback, never as a previously loaded
    /// snapshot, so token rotation and UI edits cannot clobber each other.
    /// </summary>
    public RelayConfig Update(Func<RelayConfig?, RelayConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_pathLock)
        {
            var current = LoadCore();
            var updated = update(current) ?? throw new InvalidOperationException("Settings update returned no configuration.");
            SaveCore(updated);
            return updated;
        }
    }

    /// <summary>Returns the latest profile so request handlers see rotated OAuth tokens.</summary>
    public ProviderSettings? FindProvider(string id) =>
        Load()?.Providers.FirstOrDefault(provider => provider.Id.Equals(id, StringComparison.Ordinal));

    public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = FindProvider(profileId)?.OAuthTokens;
        return ValueTask.FromResult(tokens is null
            ? null
            : new OAuthTokenSet(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtUtc,
                tokens.TokenType, tokens.Scope, tokens.IdToken));
    }

    public ValueTask SetAsync(string profileId, OAuthTokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        cancellationToken.ThrowIfCancellationRequested();
        Update(current =>
        {
            if (current is null) throw new InvalidOperationException("Provider profile settings are unavailable.");
            var found = false;
            var providers = current.Providers.Select(provider =>
            {
                if (!provider.Id.Equals(profileId, StringComparison.Ordinal)) return provider;
                found = true;
                return provider with
                {
                    OAuthTokens = new ProviderOAuthTokens(tokens.AccessToken, tokens.RefreshToken,
                        tokens.ExpiresAtUtc, tokens.Scope, tokens.TokenType, tokens.IdToken)
                };
            }).ToArray();
            if (!found) throw new InvalidOperationException("Provider profile settings are unavailable.");
            return current with { Providers = providers };
        });
        return ValueTask.CompletedTask;
    }

    public void SaveOpenAiRegistration(string profileId, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        Update(current => UpdateProvider(current, profileId, provider => provider with { OAuthClientId = clientId }));
    }

    public void SaveOpenAiIdentity(string profileId, string clientId, OpenAiAccountIdentity identity, OAuthTokenSet tokens)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tokens);
        Update(current => UpdateProvider(current, profileId, provider => provider with
        {
            AuthMode = ProviderAuthMode.OAuth,
            OAuthClientId = clientId,
            AccountId = identity.Subject,
            OAuthEmail = identity.Email,
            OAuthIssuer = identity.Issuer,
            OAuthTokens = new ProviderOAuthTokens(tokens.AccessToken, tokens.RefreshToken,
                tokens.ExpiresAtUtc, tokens.Scope, tokens.TokenType, tokens.IdToken)
        }));
    }

    public void SaveGeminiCredentials(string profileId, string clientId, string clientSecret,
        string projectId, OAuthTokenSet tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(tokens);
        Update(current => UpdateProvider(current, profileId, provider =>
        {
            if (!provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Gemini credentials can only be saved to a Gemini provider profile.");
            return provider with
            {
                AuthMode = ProviderAuthMode.OAuth,
                OAuthClientId = clientId,
                OAuthClientSecret = clientSecret,
                ProjectId = projectId,
                OAuthIssuer = GeminiOAuth.Issuer,
                OAuthTokens = new ProviderOAuthTokens(tokens.AccessToken, tokens.RefreshToken,
                    tokens.ExpiresAtUtc, tokens.Scope, tokens.TokenType, tokens.IdToken)
            };
        }));
    }

    private static RelayConfig UpdateProvider(RelayConfig? current, string profileId, Func<ProviderSettings, ProviderSettings> update)
    {
        if (current is null) throw new InvalidOperationException("Provider profile settings are unavailable.");
        var found = false;
        var providers = current.Providers.Select(provider =>
        {
            if (!provider.Id.Equals(profileId, StringComparison.Ordinal)) return provider;
            found = true;
            return update(provider);
        }).ToArray();
        if (!found) throw new InvalidOperationException("Provider profile settings are unavailable.");
        return current with { Providers = providers };
    }


    /// <summary>Creates and persists one stable OAuth host identity for this installation.</summary>
    public string GetOrCreateOAuthHostId() => Update(current =>
    {
        var config = current ?? new RelayConfig(
            RelayConfig.CurrentSchemaVersion, RelayProtocol.CreateLocalKey(), []);
        return string.IsNullOrWhiteSpace(config.OAuthHostId)
            ? config with { OAuthHostId = $"urn:uuid:{Guid.NewGuid():D}" }
            : config;
    }).OAuthHostId!;

    private RelayConfig? LoadCore()
    {
        _lastLoadError = null;
        try
        {
            if (!File.Exists(_path)) return null;
            var protectedBytes = File.ReadAllBytes(_path);
            var json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return RelayConfigReader.FromJson(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Preserve the corrupt file by moving it to a .bad-* path with timestamp
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                var badPath = _path + ".bad-" + timestamp;
                File.Move(_path, badPath);
                _lastLoadError = $"Settings could not be read and were preserved at {badPath}";
            }
            catch (Exception moveEx)
            {
                _lastLoadError = $"Settings could not be read and could not be preserved: {moveEx.Message}";
            }
            return null;
        }
    }

    public void Save(RelayConfig config)
    {
        lock (_pathLock) SaveCore(config);
    }

    private void SaveCore(RelayConfig config)
    {
        var directory = System.IO.Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.SerializeToUtf8Bytes(config);
        var protectedBytes = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        var temporaryPath = _path + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _path, true);
    }

    public void Delete()
    {
        lock (_pathLock)
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
