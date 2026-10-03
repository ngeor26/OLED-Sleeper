using OLED_Sleeper.Features.MonitorIdleDetection.Models;
using OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;
using Serilog;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services;

/// <summary>Refreshes media sessions and audio fallback away from the idle loop.</summary>
public sealed class PlaybackActivityService(IAudioPlaybackDetector audioDetector, IPlaybackSnapshotSource snapshotSource, TimeProvider timeProvider) : IPlaybackActivityService
{
    private readonly object _gate = new();
    private readonly PlaybackWindowSelector _selector = new();
    private IReadOnlyList<PlaybackWindow> _playing = Array.Empty<PlaybackWindow>();
    private Task? _refresh;
    private DateTime _lastRefresh;
    private DateTime _lastWarning;
    private readonly Dictionary<nint, DateTime> _lastPlaying = new();
    private string _lastReportedPlayback = string.Empty;

    public IReadOnlyList<PlaybackWindow> GetPlayingWindows(nint foregroundWindow)
    {
        lock (_gate)
        {
            if ((_refresh == null || _refresh.IsCompleted) && timeProvider.GetUtcNow().UtcDateTime - _lastRefresh >= TimeSpan.FromMilliseconds(500))
            {
                _lastRefresh = timeProvider.GetUtcNow().UtcDateTime;
                _refresh = Task.Run(() => RefreshAsync(foregroundWindow));
            }
            return _playing;
        }
    }

    internal async Task RefreshAsync(nint foregroundWindow)
    {
        try
        {
            var windows = snapshotSource.ReadWindows();
            var selected = _selector.Select(windows, foregroundWindow);
            IReadOnlyList<MediaPlaybackSession> sessions = Array.Empty<MediaPlaybackSession>();
            try
            {
                sessions = await snapshotSource.ReadMediaSessionsAsync();
            }
            catch (Exception ex)
            {
                Warn(ex, "Windows media sessions unavailable; using audio playback fallback.");
            }

            var playing = new List<PlaybackWindow>();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var window in selected)
            {
                var matched = sessions.Where(s => PlaybackWindowSelector.MatchesApplication(window, s.AppId)).ToList();
                // A published pause is authoritative: some applications keep an audio stream open while paused.
                bool active = matched.Count > 0 ? matched.Any(s => s.Playing) : audioDetector.IsProcessPlayingAudio(window.ProcessId);
                if (active) _lastPlaying[window.Handle] = now;
                if (active || (matched.Count == 0 && _lastPlaying.TryGetValue(window.Handle, out var last) && now - last < TimeSpan.FromSeconds(2)))
                    playing.Add(window);
            }
            foreach (var handle in _lastPlaying.Keys.Except(windows.Select(w => w.Handle)).ToList()) _lastPlaying.Remove(handle);
            var description = string.Join("; ", playing.Select(w => $"{w.ProcessName} PID {w.ProcessId}, HWND {w.Handle}, bounds {w.Bounds}"));
            if (description != _lastReportedPlayback)
            {
                Log.Debug("Playback windows changed: {PlaybackWindows}. Published media sessions: {MediaSessionCount}.",
                    string.IsNullOrEmpty(description) ? "none" : description, sessions.Count);
                _lastReportedPlayback = description;
            }
            lock (_gate)
            {
                _playing = playing;
                _lastRefresh = now;
            }
        }
        catch (Exception ex)
        {
            Warn(ex, "Could not refresh playback windows.");
            lock (_gate) _playing = Array.Empty<PlaybackWindow>();
        }
    }

    private void Warn(Exception exception, string message)
    {
        if (DateTime.UtcNow - _lastWarning < TimeSpan.FromMinutes(1)) return;
        _lastWarning = DateTime.UtcNow;
        Log.Warning(exception, message);
    }
}
