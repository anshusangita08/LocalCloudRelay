namespace LocalCloudRelay;

public sealed record ProviderPreset(string Label, string Name, string Kind, string BaseUrl, string? Note = null);

/// <summary>
/// Known upstreams, offered in the Add/Edit dialog so a base URL with a path prefix
/// does not have to be typed exactly right. Purely a convenience: anything here can
/// be typed by hand instead, and the list is not the only way to add a provider.
/// </summary>
public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new("OpenCode Zen", "OpenCode Zen", ProviderKinds.OpenAi, "https://opencode.ai/zen/v1",
            "Curated models. Serves /responses, /messages, /chat/completions and /models/{model}."),
        new("OpenCode Go", "OpenCode Go", ProviderKinds.OpenAi, "https://opencode.ai/zen/go/v1",
            "Open models subscription. Same API shapes as Zen. Sends x-opencode-session when a client supplies one."),
        new("Ollama", "Local Ollama", ProviderKinds.Local, "http://127.0.0.1:11434/v1", "Local, so never charged a token price."),
        new("LM Studio", "Local LM Studio", ProviderKinds.Local, "http://127.0.0.1:1234/v1", "Local, so never charged a token price."),
        new("LiteLLM (default)", "LiteLLM", ProviderKinds.OpenAi, "http://127.0.0.1:4000"),
        new("Custom", "", "", "")
    ];

    public static ProviderPreset? Find(string label) =>
        All.FirstOrDefault(p => p.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
}
