using OLED_Sleeper.Features.MonitorIdleDetection.Models;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;

public interface IPlaybackSnapshotSource
{
    IReadOnlyList<PlaybackWindow> ReadWindows();
    Task<IReadOnlyList<MediaPlaybackSession>> ReadMediaSessionsAsync();
}
