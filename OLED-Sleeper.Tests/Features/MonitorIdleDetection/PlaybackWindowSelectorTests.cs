using OLED_Sleeper.Features.MonitorIdleDetection.Models;
using OLED_Sleeper.Features.MonitorIdleDetection.Services;
using System.Windows;

namespace OLED_Sleeper.Tests.Features.MonitorIdleDetection;

public class PlaybackWindowSelectorTests
{
    private static PlaybackWindow Window(int handle, uint process, int x = 0) =>
        new(handle, process, "brave", "", new Rect(x, 0, 1920, 1080));

    [Fact]
    public void SingleWindowIsDetectedWithoutForegroundFocus()
    {
        var window = Window(1, 10, 1920);
        Assert.Equal(window, Assert.Single(new PlaybackWindowSelector().Select([window], 99)));
    }

    [Fact]
    public void AttributionSurvivesFocusChangesAndInactivePlaybackChecks()
    {
        var selector = new PlaybackWindowSelector();
        PlaybackWindow[] windows = [Window(1, 10), Window(2, 10, 1920), Window(3, 20)];
        selector.Select(windows, 2); // Remember the video window before playback is detected.
        selector.Select(windows, 3); // Another app takes focus while playback is paused/buffering.
        var selected = selector.Select(windows, 3);
        Assert.Contains(windows[1], selected);
        Assert.DoesNotContain(windows[0], selected);
    }

    [Fact]
    public void MovingWindowUsesItsCurrentBounds()
    {
        var selector = new PlaybackWindowSelector();
        var window = Window(1, 10);
        selector.Select([window], 1);
        var moved = window with { Bounds = new Rect(1920, 0, 1920, 1080) };
        Assert.Equal(moved.Bounds, Assert.Single(selector.Select([moved], 99)).Bounds);
    }

    [Fact]
    public void AmbiguousWindowsWithoutFocusHistoryAreNotGuessed()
    {
        Assert.Empty(new PlaybackWindowSelector().Select([Window(1, 10), Window(2, 10)], 99));
    }

    [Theory]
    [InlineData("brave", true)]
    [InlineData("Brave.exe", true)]
    [InlineData("notbrave", false)]
    public void DesktopSessionMatchesExactExecutableIdentity(string source, bool expected)
    {
        Assert.Equal(expected, PlaybackWindowSelector.MatchesApplication(Window(1, 10), source));
    }

    [Fact]
    public void PackagedSessionMatchesAppUserModelId()
    {
        var window = Window(1, 10) with { AppId = "PlayerPackage!App" };
        Assert.True(PlaybackWindowSelector.MatchesApplication(window, "PlayerPackage!App"));
        Assert.False(PlaybackWindowSelector.MatchesApplication(window, "OtherPackage!App"));
    }
}
