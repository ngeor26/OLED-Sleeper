using OLED_Sleeper.Features.MonitorIdleDetection.Models;
using OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;
using Windows.Media.Control;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services;

public sealed class WindowsPlaybackSnapshotSource : IPlaybackSnapshotSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public IReadOnlyList<PlaybackWindow> ReadWindows() => PlaybackWindowReader.Read();

    public async Task<IReadOnlyList<MediaPlaybackSession>> ReadMediaSessionsAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager.GetSessions().Select(session => new MediaPlaybackSession(session.SourceAppUserModelId,
                session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)).ToArray();
        }
        catch
        {
            _manager = null;
            throw;
        }
    }
}
