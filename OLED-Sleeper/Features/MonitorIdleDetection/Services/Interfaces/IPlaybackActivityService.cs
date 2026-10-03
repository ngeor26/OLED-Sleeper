using OLED_Sleeper.Features.MonitorIdleDetection.Models;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;

public interface IPlaybackActivityService
{
    IReadOnlyList<PlaybackWindow> GetPlayingWindows(nint foregroundWindow);
}
