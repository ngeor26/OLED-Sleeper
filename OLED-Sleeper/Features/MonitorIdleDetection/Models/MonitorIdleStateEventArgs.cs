using OLED_Sleeper.Features.UserSettings.Models;
using System.Windows;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Models
{
    /// <summary>
    /// Provides event data for monitor idle/active state transitions.
    /// Used by services to communicate monitor state changes, including context for the event.
    /// </summary>
    public class MonitorIdleStateEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the unique hardware ID of the monitor.
        /// </summary>
        public string HardwareId { get; }

        /// <summary>
        /// Gets the display number of the monitor.
        /// </summary>
        public int DisplayNumber { get; }

        /// <summary>
        /// Gets the bounds of the monitor in screen coordinates.
        /// </summary>
        public Rect Bounds { get; }

        /// <summary>
        /// Gets the user-configured settings for the monitor.
        /// </summary>
        public MonitorSettings Settings { get; }

        /// <summary>
        /// Gets the handle of the foreground window at the time of the event.
        /// </summary>
        public nint ForegroundWindowHandle { get; }

        /// <summary>
        /// Gets the process ID of the foreground window.
        /// </summary>
        public uint ForegroundProcessId { get; }

        /// <summary>
        /// Gets whether an active audio session was found for the foreground application or its descendants.
        /// </summary>
        public bool IsForegroundProcessPlayingAudio { get; }

        /// <summary>
        /// Gets the process associated with the monitor's currently tracked audio playback.
        /// </summary>
        public uint AudioPlaybackProcessId { get; }

        /// <summary>
        /// Gets whether the tracked application's audio session is currently active.
        /// </summary>
        public bool IsTrackedAudioPlaying { get; }

        /// <summary>
        /// Gets the foreground window bounds at the time of the event.
        /// </summary>
        public Rect ForegroundWindowBounds { get; }

        /// <summary>
        /// Gets the reason why the monitor is considered active (e.g., mouse, window, input).
        /// </summary>
        public ActivityReason Reason { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the event should be ignored by the sender.
        /// Subscribers can set this to true to prevent the sender from changing its internal state.
        /// </summary>
        public bool IsIgnored { get; set; } = false;

        /// <param name="hardwareId">The unique hardware ID of the monitor.</param>
        /// <param name="displayNumber">The display number of the monitor.</param>
        /// <param name="bounds">The bounds of the monitor in screen coordinates.</param>
        /// <param name="settings">The user-configured settings for the monitor.</param>
        /// <param name="foregroundWindowHandle">The handle of the foreground window at the time of the event.</param>
        /// <param name="reason">The reason why the monitor is considered active.</param>
        /// <param name="foregroundProcessId">The foreground window process ID.</param>
        /// <param name="isForegroundProcessPlayingAudio">Whether an active audio session was found.</param>
        /// <param name="foregroundWindowBounds">The foreground window bounds at the time of the event.</param>
        /// <param name="audioPlaybackProcessId">The audio process currently associated with the monitor.</param>
        /// <param name="isTrackedAudioPlaying">Whether the tracked process currently has active audio.</param>
        public MonitorIdleStateEventArgs(
            string hardwareId,
            int displayNumber,
            Rect bounds,
            MonitorSettings settings,
            nint foregroundWindowHandle,
            ActivityReason reason,
            uint foregroundProcessId = 0,
            bool isForegroundProcessPlayingAudio = false,
            Rect foregroundWindowBounds = default,
            uint audioPlaybackProcessId = 0,
            bool isTrackedAudioPlaying = false)
        {
            HardwareId = hardwareId;
            DisplayNumber = displayNumber;
            Bounds = bounds;
            Settings = settings;
            ForegroundWindowHandle = foregroundWindowHandle;
            Reason = reason;
            ForegroundProcessId = foregroundProcessId;
            IsForegroundProcessPlayingAudio = isForegroundProcessPlayingAudio;
            ForegroundWindowBounds = foregroundWindowBounds;
            AudioPlaybackProcessId = audioPlaybackProcessId;
            IsTrackedAudioPlaying = isTrackedAudioPlaying;
        }
    }
}