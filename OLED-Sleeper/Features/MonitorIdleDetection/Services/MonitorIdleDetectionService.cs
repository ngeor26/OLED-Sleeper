using OLED_Sleeper.Features.MonitorBehavior.Commands;
using OLED_Sleeper.Features.MonitorIdleDetection.Models;
using OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;
using OLED_Sleeper.Features.MonitorInformation.Models;
using OLED_Sleeper.Features.UserSettings.Models;
using OLED_Sleeper.Messaging.Interfaces;
using OLED_Sleeper.Native;
using Serilog;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services
{
    /// <summary>
    /// Monitors user activity and determines when managed monitors become idle or active.
    /// Handles per-monitor state machines and dispatches commands to apply idle/active behaviors.
    /// </summary>
    public class MonitorIdleDetectionService : IMonitorIdleDetectionService
    {
        /// <summary>
        /// Window classes that make up the shell desktop. The desktop spans the whole virtual screen, so
        /// it never counts as active-window activity for any monitor.
        /// </summary>
        private static readonly HashSet<string> DesktopWindowClasses = new(StringComparer.Ordinal)
        {
            "Progman",
            "WorkerW"
        };

        /// <summary>
        /// Buffer size, in characters, for a window class name.
        /// </summary>
        private const int ClassNameBufferLength = 256;

        // === Dependencies & State ===
        private readonly IMediator _mediator;
        private readonly IPlaybackActivityService _playbackActivityService;
        private IReadOnlyList<PlaybackWindow> _playingWindows = Array.Empty<PlaybackWindow>();

        private CancellationTokenSource? _cancellationTokenSource;
        private List<ManagedMonitorState> _managedMonitors = new();
        private readonly object _lock = new();
        private readonly Dictionary<string, MonitorTimerState> _monitorStates = new();

        // === Construction ===

        public MonitorIdleDetectionService(IMediator mediator, IPlaybackActivityService playbackActivityService)
        {
            _mediator = mediator;
            _playbackActivityService = playbackActivityService;
        }

        // === Service Lifecycle ===

        /// <summary>
        /// Starts the idle detection service and begins monitoring. Any previous loop is stopped first.
        /// </summary>
        public void Start()
        {
            Stop();

            var cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource = cancellationTokenSource;
            Task.Run(() => IdleCheckLoop(cancellationTokenSource.Token));
            Log.Information("MonitorIdleDetectionService started.");
        }

        /// <summary>
        /// Stops the idle detection service and monitoring. A second call does nothing.
        /// </summary>
        public void Stop()
        {
            var cancellationTokenSource = Interlocked.Exchange(ref _cancellationTokenSource, null);
            if (cancellationTokenSource == null) return;

            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
            Log.Information("MonitorIdleDetectionService stopped.");
        }

        /// <summary>
        /// Updates the settings for all managed monitors.
        /// </summary>
        /// <param name="monitorSettings">The list of monitor settings to manage.</param>
        /// <param name="monitors">The monitors to join the settings against, supplying bounds and display numbers.</param>
        /// <remarks>
        /// Callers supply the monitor list; this class never reads the shared cache and does not depend on
        /// the monitor manager, so no lock that manager owns can be taken while <see cref="_lock"/> is held.
        /// </remarks>
        public Task UpdateSettingsAsync(List<MonitorSettings> monitorSettings, IReadOnlyList<MonitorInfo> monitors)
        {
            var activeSettings = monitorSettings.Where(s => s.IsManaged).ToList();

            int trackedCount;
            lock (_lock)
            {
                _managedMonitors = (from setting in activeSettings
                                    join monitorInfo in monitors on setting.HardwareId equals monitorInfo.HardwareId
                                    select new ManagedMonitorState
                                    {
                                        Settings = setting,
                                        Bounds = monitorInfo.Bounds,
                                        DisplayNumber = monitorInfo.DisplayNumber
                                    }).ToList();

                _monitorStates.Clear();
                foreach (var monitor in _managedMonitors)
                {
                    _monitorStates[monitor.Settings.HardwareId] = new MonitorTimerState
                    {
                        DisplayNumber = monitor.DisplayNumber
                    };
                }

                trackedCount = _managedMonitors.Count;
            }

            Log.Information("MonitorIdleDetectionService settings updated. Now tracking {Count} monitors.", trackedCount);
            foreach (var monitor in _managedMonitors)
            {
                Log.Debug(
                    "Idle detection settings for monitor #{DisplayNumber} ({HardwareId}): managed {IsManaged}, behavior {Behavior}, idle timeout {IdleTimeMilliseconds} ms, audio playback keeps active {IsActiveOnAudioPlayback}.",
                    monitor.DisplayNumber,
                    monitor.Settings.HardwareId,
                    monitor.Settings.IsManaged,
                    monitor.Settings.Behavior,
                    monitor.Settings.IdleTimeMilliseconds,
                    monitor.Settings.IsActiveOnAudioPlayback);
            }

            return Task.CompletedTask;
        }

        // === Idle Detection Loop ===

        /// <summary>
        /// Main background loop that periodically checks monitor states.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        private async Task IdleCheckLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ProcessMonitors();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error occurred in the idle check loop.");
                }
                await Task.Delay(200, token);
            }
        }

        /// <summary>
        /// Gathers system state and processes each managed monitor according to the state machine logic.
        /// </summary>
        private void ProcessMonitors()
        {
            bool checkAudioPlayback;
            lock (_lock)
            {
                checkAudioPlayback = _managedMonitors.Any(m => m.Settings.IsActiveOnAudioPlayback);
            }

            var systemState = GetSystemState();
            var playingWindows = checkAudioPlayback ? _playbackActivityService.GetPlayingWindows(systemState.ForegroundWindowHandle) : Array.Empty<PlaybackWindow>();

            lock (_lock)
            {
                _playingWindows = playingWindows;
                foreach (var monitor in _managedMonitors)
                {
                    ProcessSingleMonitor(monitor, systemState);
                }
            }
        }

        /// <summary>
        /// Processes a single managed monitor according to the state machine logic.
        /// </summary>
        /// <param name="monitor">The managed monitor.</param>
        /// <param name="systemState">Current system state.</param>
        private void ProcessSingleMonitor(ManagedMonitorState monitor, SystemState systemState)
        {
            var timerState = _monitorStates[monitor.Settings.HardwareId];
            var activityReason = GetActivityReason(monitor, timerState, systemState);
            bool hasActivityNow = activityReason != ActivityReason.None;

            var eventArgs = new MonitorIdleStateEventArgs(
                monitor.Settings.HardwareId, monitor.DisplayNumber, monitor.Bounds,
                monitor.Settings, systemState.ForegroundWindowHandle, activityReason,
                systemState.ForegroundProcessId,
                systemState.IsForegroundProcessPlayingAudio,
                systemState.ForegroundWindowRect,
                timerState.AudioPlaybackProcessId,
                activityReason == ActivityReason.MediaPlayback);

            if (timerState.LastActivityReason != activityReason)
            {
                Rect foregroundIntersection = Rect.Intersect(monitor.Bounds, systemState.ForegroundWindowRect);
                Log.Debug(
                    "Monitor #{DisplayNumber} activity reason changed from {PreviousReason} to {ActivityReason}. Audio option {AudioOptionEnabled}, foreground PID {ForegroundProcessId}, foreground audio detected {ForegroundAudioDetected}, tracked audio PID {TrackedAudioProcessId}, tracked audio active {TrackedAudioActive}, window overlaps monitor {WindowOverlapsMonitor}, monitor bounds {MonitorBounds}, foreground window bounds {ForegroundWindowBounds}.",
                    monitor.DisplayNumber,
                    timerState.LastActivityReason?.ToString() ?? "Uninitialized",
                    activityReason,
                    monitor.Settings.IsActiveOnAudioPlayback,
                    systemState.ForegroundProcessId,
                    systemState.IsForegroundProcessPlayingAudio,
                    timerState.AudioPlaybackProcessId,
                    activityReason == ActivityReason.MediaPlayback,
                    !foregroundIntersection.IsEmpty && foregroundIntersection.Width > 0 && foregroundIntersection.Height > 0,
                    monitor.Bounds,
                    systemState.ForegroundWindowRect);
                timerState.LastActivityReason = activityReason;
            }

            switch (timerState.CurrentState)
            {
                case MonitorStateMachine.Active:
                    HandleActiveState(timerState, hasActivityNow);
                    break;

                case MonitorStateMachine.Counting:
                    HandleCountingState(timerState, monitor, hasActivityNow, eventArgs);
                    break;

                case MonitorStateMachine.Idle:
                    HandleIdleState(timerState, monitor, hasActivityNow, eventArgs);
                    break;
            }
        }

        // === State Machine Handlers ===

        /// <summary>
        /// Handles the Active state for a monitor. Transitions to Counting if no activity is detected.
        /// </summary>
        /// <param name="timerState">The timer state for the monitor.</param>
        /// <param name="hasActivityNow">Whether activity is currently detected.</param>
        private void HandleActiveState(MonitorTimerState timerState, bool hasActivityNow)
        {
            if (!hasActivityNow)
            {
                timerState.CurrentState = MonitorStateMachine.Counting;
                timerState.ActivityStoppedTimestamp = DateTime.UtcNow;
                Log.Debug("Monitor #{DisplayNumber} transitioned Active -> Counting because no enabled activity condition matched.",
                    timerState.DisplayNumber);
            }
        }

        /// <summary>
        /// Handles the Counting state for a monitor. If idle time is reached, transitions to Idle and dispatches idle behavior command.
        /// </summary>
        /// <param name="timerState">The timer state for the monitor.</param>
        /// <param name="monitor">The managed monitor.</param>
        /// <param name="hasActivityNow">Whether activity is currently detected.</param>
        /// <param name="eventArgs">Monitor idle state event arguments.</param>
        private void HandleCountingState(MonitorTimerState timerState, ManagedMonitorState monitor, bool hasActivityNow, MonitorIdleStateEventArgs eventArgs)
        {
            if (hasActivityNow)
            {
                Log.Debug("Monitor #{DisplayNumber} transitioned Counting -> Active because of {ActivityReason}.",
                    monitor.DisplayNumber, eventArgs.Reason);
                timerState.CurrentState = MonitorStateMachine.Active;
                Log.Debug("Monitor #{DisplayNumber} idle countdown was reset by activity reason {ActivityReason}.",
                    monitor.DisplayNumber, eventArgs.Reason);
            }
            else
            {
                var elapsed = DateTime.UtcNow - timerState.ActivityStoppedTimestamp;
                if (elapsed.TotalMilliseconds >= monitor.Settings.IdleTimeMilliseconds)
                {
                    timerState.CurrentState = MonitorStateMachine.Idle;
                    Log.Information(
                        "Monitor #{DisplayNumber} reached its idle timeout after {Seconds}s. Dispatching behavior {Behavior}; audio option enabled {AudioOptionEnabled}, foreground process {ForegroundProcessId}, foreground audio detected {ForegroundAudioDetected}, tracked audio PID {TrackedAudioProcessId}, tracked audio active {TrackedAudioActive}, foreground window bounds {ForegroundWindowBounds}, monitor bounds {MonitorBounds}.",
                        monitor.DisplayNumber,
                        Math.Round(elapsed.TotalSeconds),
                        monitor.Settings.Behavior,
                        monitor.Settings.IsActiveOnAudioPlayback,
                        eventArgs.ForegroundProcessId,
                        eventArgs.IsForegroundProcessPlayingAudio,
                        eventArgs.AudioPlaybackProcessId,
                        eventArgs.IsTrackedAudioPlaying,
                        eventArgs.ForegroundWindowBounds,
                        monitor.Bounds);
                    _mediator.SendAsync(new ApplyMonitorIdleBehaviorCommand(eventArgs));
                }
            }
        }

        /// <summary>
        /// Handles the Idle state for a monitor. If activity is detected, transitions to Active and dispatches active behavior command.
        /// </summary>
        /// <param name="timerState">The timer state for the monitor.</param>
        /// <param name="monitor">The managed monitor.</param>
        /// <param name="hasActivityNow">Whether activity is currently detected.</param>
        /// <param name="eventArgs">Monitor idle state event arguments.</param>
        private void HandleIdleState(MonitorTimerState timerState, ManagedMonitorState monitor, bool hasActivityNow, MonitorIdleStateEventArgs eventArgs)
        {
            if (hasActivityNow)
            {
                _mediator.SendAsync(new ApplyMonitorActiveBehaviorCommand(eventArgs));
                if (!eventArgs.IsIgnored)
                {
                    timerState.CurrentState = MonitorStateMachine.Active;
                    Log.Information("Monitor #{DisplayNumber} is now ACTIVE because of {ActivityReason}.",
                        monitor.DisplayNumber, eventArgs.Reason);
                }
            }
        }

        // === Activity Detection Helpers ===

        /// <summary>
        /// Determines the reason for any qualifying activity on a monitor at this moment.
        /// </summary>
        /// <param name="monitor">The managed monitor.</param>
        /// <param name="state">Current system state.</param>
        /// <returns>The activity reason.</returns>
        private ActivityReason GetActivityReason(ManagedMonitorState monitor, MonitorTimerState timerState, SystemState state)
        {
            if (monitor.Settings.KeepAwake) return ActivityReason.KeepAwake;
            var playbackWindow = monitor.Settings.IsActiveOnAudioPlayback ? _playingWindows.FirstOrDefault(window =>
            {
                var overlap = Rect.Intersect(monitor.Bounds, window.Bounds);
                return !overlap.IsEmpty && overlap.Width > 0 && overlap.Height > 0;
            }) : null;
            timerState.AudioPlaybackProcessId = playbackWindow?.ProcessId ?? 0;
            if (playbackWindow != null)
                return ActivityReason.MediaPlayback;
            if (IsSystemInputActive(monitor, state))
                return ActivityReason.SystemInput;
            if (IsMousePositionActive(monitor, state))
                return ActivityReason.MousePosition;
            if (IsActiveWindowActive(monitor, state))
                return ActivityReason.ActiveWindow;
            return ActivityReason.None;
        }

        /// <summary>
        /// Checks if system input should be considered activity for the monitor.
        /// </summary>
        private static bool IsSystemInputActive(ManagedMonitorState monitor, SystemState state)
        {
            return monitor.Settings.IsActiveOnInput && state.IdleTimeMilliseconds < monitor.Settings.IdleTimeMilliseconds;
        }

        /// <summary>
        /// Checks if mouse position should be considered activity for the monitor.
        /// </summary>
        private static bool IsMousePositionActive(ManagedMonitorState monitor, SystemState state)
        {
            return monitor.Settings.IsActiveOnMousePosition && monitor.Bounds.Contains(state.CursorPosition);
        }

        /// <summary>
        /// Checks if the active window should be considered activity for the monitor.
        /// </summary>
        private static bool IsActiveWindowActive(ManagedMonitorState monitor, SystemState state)
        {
            if (monitor.Settings.IsActiveOnActiveWindow)
            {
                Rect intersection = Rect.Intersect(monitor.Bounds, state.ForegroundWindowRect);
                if (!intersection.IsEmpty && intersection.Width > 0 && intersection.Height > 0)
                    return true;
            }
            return false;
        }

        // === System State Helpers ===

        /// <summary>
        /// Gathers all required system-wide state information at once.
        /// </summary>
        /// <returns>System state snapshot.</returns>
        private SystemState GetSystemState()
        {
            uint idleTime = GetSystemIdleTimeMilliseconds();
            NativeMethods.GetCursorPos(out var nativePoint);
            Point cursorPosition = new(nativePoint.X, nativePoint.Y);
            nint foregroundWindowHandle = NativeMethods.GetForegroundWindow();
            Rect windowRect = IsDesktopWindow(foregroundWindowHandle)
                ? Rect.Empty
                : GetForegroundWindowRect(foregroundWindowHandle);
            NativeMethods.GetWindowThreadProcessId(foregroundWindowHandle, out uint foregroundProcessId);
            return new SystemState(idleTime, cursorPosition, windowRect, foregroundWindowHandle, foregroundProcessId, false);
        }

        /// <summary>
        /// Determines whether a window is the shell desktop rather than an application window.
        /// </summary>
        /// <param name="hwnd">The handle to test.</param>
        /// <returns>True when the window belongs to one of the <see cref="DesktopWindowClasses"/>; false for a null handle or a class name the shell would not answer.</returns>
        private static bool IsDesktopWindow(nint hwnd)
        {
            if (hwnd == nint.Zero) return false;

            var className = new StringBuilder(ClassNameBufferLength);
            if (NativeMethods.GetClassName(hwnd, className, className.Capacity) == 0) return false;

            return DesktopWindowClasses.Contains(className.ToString());
        }

        /// <summary>
        /// Gets the rectangle of the foreground window.
        /// </summary>
        /// <param name="foregroundWindowHandle">The handle to the foreground window.</param>
        /// <returns>The window rectangle.</returns>
        private static Rect GetForegroundWindowRect(nint foregroundWindowHandle)
        {
            if (NativeMethods.DwmGetWindowAttribute(foregroundWindowHandle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var nativeWindowRect, Marshal.SizeOf(typeof(NativeMethods.Rect))) == 0)
            {
                return nativeWindowRect.ToWindowsRect();
            }
            else
            {
                NativeMethods.GetWindowRect(foregroundWindowHandle, out nativeWindowRect);
                return nativeWindowRect.ToWindowsRect();
            }
        }

        /// <summary>
        /// Gets the system-wide user idle time in milliseconds using the GetLastInputInfo API.
        /// </summary>
        /// <returns>Idle time in milliseconds.</returns>
        private static uint GetSystemIdleTimeMilliseconds()
        {
            var lastInputInfo = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO)) };
            if (NativeMethods.GetLastInputInfo(ref lastInputInfo))
            {
                uint lastInputTick = lastInputInfo.dwTime;
                uint currentTick = (uint)Environment.TickCount;
                return currentTick - lastInputTick;
            }
            return 0;
        }

        // === Internal Types ===

        /// <summary>
        /// State machine for per-monitor activity.
        /// </summary>
        private enum MonitorStateMachine
        {
            Active,
            Counting,
            Idle
        }

        /// <summary>
        /// Holds state and settings for a managed monitor.
        /// </summary>
        private class ManagedMonitorState
        {
            public int DisplayNumber { get; set; }
            public required MonitorSettings Settings { get; set; }
            public Rect Bounds { get; set; }
        }

        /// <summary>
        /// Tracks timer and state for a monitor.
        /// </summary>
        private class MonitorTimerState
        {
            public MonitorStateMachine CurrentState { get; set; } = MonitorStateMachine.Active;
            public DateTime ActivityStoppedTimestamp { get; set; }
            public ActivityReason? LastActivityReason { get; set; }
            public int DisplayNumber { get; set; }
            public uint AudioPlaybackProcessId { get; set; }
        }

        /// <summary>
        /// Snapshot of system state at a point in time.
        /// </summary>
        private readonly struct SystemState
        {
            public readonly uint IdleTimeMilliseconds;
            public readonly Point CursorPosition;
            public readonly Rect ForegroundWindowRect;
            public readonly nint ForegroundWindowHandle;
            public readonly uint ForegroundProcessId;
            public readonly bool IsForegroundProcessPlayingAudio;

            public SystemState(uint idleTime, Point cursorPosition, Rect windowRect, nint foregroundWindowHandle, uint foregroundProcessId, bool isForegroundProcessPlayingAudio)
            {
                IdleTimeMilliseconds = idleTime;
                CursorPosition = cursorPosition;
                ForegroundWindowRect = windowRect;
                ForegroundWindowHandle = foregroundWindowHandle;
                ForegroundProcessId = foregroundProcessId;
                IsForegroundProcessPlayingAudio = isForegroundProcessPlayingAudio;
            }
        }
    }
}