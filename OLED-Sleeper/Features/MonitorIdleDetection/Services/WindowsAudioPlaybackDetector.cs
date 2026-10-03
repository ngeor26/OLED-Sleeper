using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;
using OLED_Sleeper.Native;
using Serilog;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services
{
    /// <summary>
    /// Reads Windows Core Audio sessions to determine whether an application or same-executable child process
    /// is rendering audio.
    /// Audio-session snapshots are refreshed asynchronously so device enumeration does not block idle detection.
    /// </summary>
    public sealed class WindowsAudioPlaybackDetector : IAudioPlaybackDetector
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

        private readonly object _lock = new();
        private AudioSnapshot? _cachedSnapshot;
        private Task? _refreshTask;
        private DateTime _lastRefreshAttemptUtc = DateTime.MinValue;
        private DateTime _lastFailureLoggedUtc = DateTime.MinValue;

        /// <inheritdoc />
        public bool IsProcessPlayingAudio(uint processId)
        {
            if (processId == 0) return false;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_cachedSnapshot == null)
                {
                    if (now - _lastRefreshAttemptUtc >= RefreshInterval)
                    {
                        _lastRefreshAttemptUtc = now;
                        try
                        {
                            _cachedSnapshot = ReadAudioSnapshot();
                        }
                        catch (Exception ex) when (ex is COMException or Win32Exception)
                        {
                            LogRefreshFailure(ex);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Unexpected error while refreshing Windows audio sessions. Keeping the previous audio snapshot.");
                        }
                    }

                    // Fail open while no reliable snapshot is available so playback is not interrupted.
                    return _cachedSnapshot?.IsProcessPlayingAudio(processId) ?? true;
                }

                if (now - _lastRefreshAttemptUtc >= RefreshInterval &&
                    (_refreshTask == null || _refreshTask.IsCompleted))
                {
                    _lastRefreshAttemptUtc = now;
                    _refreshTask = Task.Run(RefreshAudioSnapshot);
                }

                return _cachedSnapshot.IsProcessPlayingAudio(processId);
            }
        }

        private void RefreshAudioSnapshot()
        {
            try
            {
                var snapshot = ReadAudioSnapshot();
                lock (_lock)
                {
                    _cachedSnapshot = snapshot;
                }
            }
            catch (Exception ex) when (ex is COMException or Win32Exception)
            {
                LogRefreshFailure(ex);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error while refreshing Windows audio sessions. Keeping the previous audio snapshot.");
            }
        }

        private void LogRefreshFailure(Exception exception)
        {
            var now = DateTime.UtcNow;
            if (now - _lastFailureLoggedUtc < TimeSpan.FromMinutes(1)) return;

            Log.Warning(exception,
                "Could not refresh Windows audio sessions. Keeping the previous audio snapshot and treating an unavailable initial snapshot as active.");
            _lastFailureLoggedUtc = now;
        }

        private static AudioSnapshot ReadAudioSnapshot()
        {
            var processSnapshot = GetProcessSnapshot();
            var activeSessionProcessIds = new HashSet<uint>();

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    Log.Debug("Inspecting audio endpoint {EndpointName} ({EndpointId}) with {SessionCount} sessions.",
                        device.FriendlyName, device.ID, sessions.Count);

                    for (int index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        uint sessionProcessId = session.GetProcessID;
                        var sessionState = session.State;
                        processSnapshot.ProcessNames.TryGetValue(sessionProcessId, out var processName);

                        Log.Debug(
                            "Audio session on {EndpointName}: state {SessionState}, PID {ProcessId} ({ProcessName}).",
                            device.FriendlyName,
                            sessionState,
                            sessionProcessId,
                            processName ?? "unknown");

                        if (sessionState == AudioSessionState.AudioSessionStateActive && sessionProcessId != 0)
                        {
                            activeSessionProcessIds.Add(sessionProcessId);
                        }
                    }
                }
            }

            Log.Debug("Windows audio snapshot refreshed with {ActiveSessionCount} active audio sessions.",
                activeSessionProcessIds.Count);
            return new AudioSnapshot(
                activeSessionProcessIds,
                processSnapshot.ParentProcessIds,
                processSnapshot.ProcessNames);
        }

        private static ProcessSnapshot GetProcessSnapshot()
        {
            using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (snapshot.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate running processes.");

            var parentProcessIds = new Dictionary<uint, uint>();
            var processNames = new Dictionary<uint, string>();
            var entry = new NativeMethods.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>() };
            if (!NativeMethods.Process32First(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the running-process snapshot.");

            do
            {
                parentProcessIds[entry.th32ProcessID] = entry.th32ParentProcessID;
                processNames[entry.th32ProcessID] = entry.szExeFile;
                entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>();
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return new ProcessSnapshot(parentProcessIds, processNames);
        }

        private sealed record ProcessSnapshot(
            IReadOnlyDictionary<uint, uint> ParentProcessIds,
            IReadOnlyDictionary<uint, string> ProcessNames);

        private sealed class AudioSnapshot
        {
            private readonly HashSet<uint> _activeSessionProcessIds;
            private readonly IReadOnlyDictionary<uint, uint> _parentProcessIds;
            private readonly IReadOnlyDictionary<uint, string> _processNames;

            public AudioSnapshot(
                HashSet<uint> activeSessionProcessIds,
                IReadOnlyDictionary<uint, uint> parentProcessIds,
                IReadOnlyDictionary<uint, string> processNames)
            {
                _activeSessionProcessIds = activeSessionProcessIds;
                _parentProcessIds = parentProcessIds;
                _processNames = processNames;
            }

            public bool IsProcessPlayingAudio(uint processId)
            {
                _processNames.TryGetValue(processId, out var processName);
                bool isPlaying = _activeSessionProcessIds.Any(
                    sessionProcessId => IsProcessOrChild(sessionProcessId, processId, processName));

                Log.Debug(
                    "Audio snapshot lookup for process {ProcessId} ({ProcessName}) and descendants: active session found {HasActiveSession}.",
                    processId,
                    processName ?? "unknown",
                    isPlaying);
                return isPlaying;
            }

            private bool IsProcessOrChild(uint processId, uint parentProcessId, string? parentProcessName)
            {
                if (processId == parentProcessId) return true;
                if (string.IsNullOrEmpty(parentProcessName) ||
                    !_processNames.TryGetValue(processId, out var processName) ||
                    !string.Equals(processName, parentProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var currentProcessId = processId;
                var visited = new HashSet<uint>();

                while (currentProcessId != 0 && visited.Add(currentProcessId))
                {
                    if (currentProcessId == parentProcessId) return true;
                    if (!_parentProcessIds.TryGetValue(currentProcessId, out currentProcessId)) return false;
                }

                return false;
            }
        }
    }
}
