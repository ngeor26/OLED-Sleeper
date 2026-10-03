using System.Windows;

namespace OLED_Sleeper.Features.MonitorIdleDetection.Models;

public sealed record PlaybackWindow(nint Handle, uint ProcessId, string ProcessName, string AppId, Rect Bounds);
