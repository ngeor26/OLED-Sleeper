using OLED_Sleeper.Features.UserSettings.Models;
using System.Text.Json;

namespace OLED_Sleeper.Tests.Features.UserSettings.Models
{
    public class MonitorSettingsCompatibilityTests
    {
        [Fact]
        public void Deserialize_LegacyVideoPlaybackSetting_EnablesAudioPlaybackSetting()
        {
            var settings = JsonSerializer.Deserialize<MonitorSettings>(
                """{"IsActiveOnVideoPlayback":true}""");

            Assert.NotNull(settings);
            Assert.True(settings.IsActiveOnAudioPlayback);
        }

        [Fact]
        public void Serialize_DoesNotWriteLegacyVideoPlaybackSetting()
        {
            var json = JsonSerializer.Serialize(new MonitorSettings { IsActiveOnAudioPlayback = true });

            Assert.Contains("\"IsActiveOnAudioPlayback\":true", json);
            Assert.DoesNotContain("IsActiveOnVideoPlayback", json);
        }
    }
}
