using Moq;
using OLED_Sleeper.Features.MonitorIdleDetection.Models;
using OLED_Sleeper.Features.MonitorIdleDetection.Services;
using OLED_Sleeper.Features.MonitorIdleDetection.Services.Interfaces;
using System.Windows;

namespace OLED_Sleeper.Tests.Features.MonitorIdleDetection;

public class PlaybackActivityServiceTests
{
    private readonly PlaybackWindow _video = new(1, 10, "player", "", new Rect(1920, 0, 1920, 1080));
    private readonly Mock<IAudioPlaybackDetector> _audio = new();
    private readonly Mock<IPlaybackSnapshotSource> _source = new();
    private readonly Clock _clock = new();

    private PlaybackActivityService Create()
    {
        _source.Setup(s => s.ReadWindows()).Returns(() => new[] { _video });
        _source.Setup(s => s.ReadMediaSessionsAsync()).ReturnsAsync(Array.Empty<MediaPlaybackSession>());
        return new PlaybackActivityService(_audio.Object, _source.Object, _clock);
    }

    [Fact]
    public async Task MutedMediaPlaybackKeepsBackgroundWindowActiveWithoutAudio()
    {
        var service = Create();
        _source.Setup(s => s.ReadMediaSessionsAsync()).ReturnsAsync(new[] { new MediaPlaybackSession("player.exe", true) });
        await service.RefreshAsync(99);
        Assert.Equal(_video, Assert.Single(service.GetPlayingWindows(99)));
        _audio.Verify(a => a.IsProcessPlayingAudio(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task PublishedPauseOverridesAnOpenAudioStream()
    {
        var service = Create();
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(true);
        _source.Setup(s => s.ReadMediaSessionsAsync()).ReturnsAsync(new[] { new MediaPlaybackSession("player", false) });
        await service.RefreshAsync(99);
        Assert.Empty(service.GetPlayingWindows(99));
    }

    [Fact]
    public async Task AudioResumesAfterFocusChangedAndGraceExpired()
    {
        var service = Create();
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(true);
        await service.RefreshAsync(1);
        Assert.Single(service.GetPlayingWindows(99));
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(false);
        _clock.Advance(3);
        await service.RefreshAsync(99);
        Assert.Empty(service.GetPlayingWindows(99));
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(true);
        await service.RefreshAsync(99);
        Assert.Single(service.GetPlayingWindows(99));
    }

    [Fact]
    public async Task BriefAudioInterruptionDoesNotDropPlaybackWindow()
    {
        var service = Create();
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(true);
        await service.RefreshAsync(1);
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(false);
        _clock.Advance(1);
        await service.RefreshAsync(99);
        Assert.Single(service.GetPlayingWindows(99));
    }

    [Fact]
    public async Task UnavailableMediaApiFallsBackToAudio()
    {
        var service = Create();
        _source.Setup(s => s.ReadMediaSessionsAsync()).ThrowsAsync(new InvalidOperationException("Unavailable"));
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(true);
        await service.RefreshAsync(99);
        Assert.Single(service.GetPlayingWindows(99));
    }

    [Fact]
    public async Task ClosedWindowIsRemovedEvenDuringAudioGrace()
    {
        var service = Create();
        _audio.Setup(a => a.IsProcessPlayingAudio(10)).Returns(true);
        await service.RefreshAsync(1);
        _source.Setup(s => s.ReadWindows()).Returns(Array.Empty<PlaybackWindow>());
        await service.RefreshAsync(99);
        Assert.Empty(service.GetPlayingWindows(99));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
