namespace OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces
{
    /// <summary>
    /// Detects active Windows audio sessions for an application process.
    /// </summary>
    public interface IAudioPlaybackDetector
    {
        /// <summary>
        /// Returns whether the process or a same-executable child process currently owns an active render session.
        /// </summary>
        bool IsProcessPlayingAudio(uint processId);
    }
}
