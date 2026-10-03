namespace LocalCloudRelay;

/// <summary>
/// Resolves the single folder this app keeps its state in, and renames the folder left
/// behind by its previous name.
///
/// The app was called ClaudeLanRelay until v1.0.0, so an existing install has its DPAPI
/// settings there. The LAN key in that file cannot be regenerated, so losing it would
/// log every configured client out; the move is a rename, never a copy-and-delete, and
/// it only runs when the new folder is absent, so an interrupted run retries cleanly.
/// </summary>
public static class RelayPaths
{
    private const string CurrentName = "LocalCloudRelay";
    private const string LegacyName = "ClaudeLanRelay";

    private static readonly Lazy<string> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => Resolved.Value;

    public static string File(string name) => Path.Combine(DataDirectory, name);

    private static string Resolve() =>
        ResolveIn(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The rename itself, against an explicit root so it can be tested without touching
    /// the real LocalAppData.
    /// </summary>
    public static string ResolveIn(string root)
    {
        var current = Path.Combine(root, CurrentName);
        if (Directory.Exists(current)) return current;

        var legacy = Path.Combine(root, LegacyName);
        if (!Directory.Exists(legacy)) return current;

        try
        {
            Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place rather than half-moved. The app starts with default settings
            // and the old folder is still there for a human to inspect.
            return legacy;
        }

        return current;
    }
}
