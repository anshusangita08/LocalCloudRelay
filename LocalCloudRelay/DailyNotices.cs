namespace LocalCloudRelay;

/// <summary>Something the operator should know about without opening the window.</summary>
/// <param name="Key">Identifies the event for de-duplication, such as "budget|coding".</param>
/// <param name="Title">Short notification title.</param>
/// <param name="Message">One or two plain sentences saying what happened and what to do.</param>
public sealed record RelayNotice(string Key, string Title, string Message);

/// <summary>
/// Lets each kind of notice through once per local day. A router over budget refuses
/// every request until midnight, and a refused sign-in fails every turn; without this
/// the tray would show the same balloon on each one.
/// </summary>
public sealed class DailyNotices(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _day;

    /// <summary>True the first time a key is seen on a local day, false after that.</summary>
    public bool ShouldShow(string key)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            if (today != _day)
            {
                _day = today;
                _shown.Clear();
            }
            return _shown.Add(key);
        }
    }
}
