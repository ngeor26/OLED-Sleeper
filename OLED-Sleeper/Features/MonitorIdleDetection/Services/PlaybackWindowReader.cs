using OLED_Sleeper.Features.MonitorIdleDetection.Models;
using OLED_Sleeper.Native;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Services;

internal static class PlaybackWindowReader
{
    public static IReadOnlyList<PlaybackWindow> Read()
    {
        var windows = new List<PlaybackWindow>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle) || NativeMethods.GetWindowTextLength(handle) == 0) return true;
            if (NativeMethods.DwmGetWindowCloaked(handle, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                string appId = ReadAppId(process);
                if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<NativeMethods.Rect>()) != 0)
                    NativeMethods.GetWindowRect(handle, out rect);
                var bounds = rect.ToWindowsRect();
                if (bounds.Width > 0 && bounds.Height > 0)
                    windows.Add(new PlaybackWindow(handle, processId, process.ProcessName, appId, bounds));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                // A process may exit or deny access during enumeration.
            }
            return true;
        }, nint.Zero);
        return windows;
    }

    private static string ReadAppId(Process process)
    {
        try
        {
            uint length = 512;
            var buffer = new StringBuilder((int)length);
            return NativeMethods.GetApplicationUserModelId(process.Handle, ref length, buffer) == 0 ? buffer.ToString() : string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access to a process handle is not necessary for executable-name or audio matching.
            return string.Empty;
        }
    }
}
