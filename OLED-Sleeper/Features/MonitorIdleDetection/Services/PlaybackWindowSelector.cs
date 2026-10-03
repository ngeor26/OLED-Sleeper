using OLED_Sleeper.Features.MonitorIdleDetection.Models;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services;

/// <summary>
/// Remembers window attribution independently of playback. A single visible window is unambiguous;
/// for multiple windows belonging to one process, use the last focused window rather than every monitor.
/// </summary>
public sealed class PlaybackWindowSelector
{
    private readonly Dictionary<uint, nint> _lastFocused = new();

    public IReadOnlyList<PlaybackWindow> Select(IReadOnlyList<PlaybackWindow> windows, nint foregroundWindow)
    {
        var foreground = windows.FirstOrDefault(w => w.Handle == foregroundWindow);
        if (foreground != null) _lastFocused[foreground.ProcessId] = foreground.Handle;

        var result = new List<PlaybackWindow>();
        foreach (var group in windows.GroupBy(w => w.ProcessId))
        {
            var candidates = group.ToList();
            if (candidates.Count == 1) result.Add(candidates[0]);
            else if (_lastFocused.TryGetValue(group.Key, out var handle))
            {
                var selected = candidates.FirstOrDefault(w => w.Handle == handle);
                if (selected != null) result.Add(selected);
            }
        }

        foreach (var processId in _lastFocused.Keys.Except(windows.Select(w => w.ProcessId)).ToList())
            _lastFocused.Remove(processId);
        return result;
    }

    public static bool MatchesApplication(PlaybackWindow window, string sourceAppId) =>
        (!string.IsNullOrEmpty(window.AppId) && string.Equals(window.AppId, sourceAppId, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(window.ProcessName, sourceAppId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(window.ProcessName + ".exe", sourceAppId, StringComparison.OrdinalIgnoreCase);
}
