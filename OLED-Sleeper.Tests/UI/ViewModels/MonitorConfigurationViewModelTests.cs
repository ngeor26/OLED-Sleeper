using OLED_Sleeper.Features.MonitorInformation.Models;
using OLED_Sleeper.Features.UserSettings.Models;
using OLED_Sleeper.UI.ViewModels;

namespace OLED_Sleeper.Tests.UI.ViewModels
{
    public class MonitorConfigurationViewModelTests
    {
        [Fact]
        public void KeepAwake_IsSavedTrackedAndReverted()
        {
            var viewModel = new MonitorConfigurationViewModel(new MonitorInfo { HardwareId = "MON-1" });
            viewModel.KeepAwake = true;
            Assert.True(viewModel.IsDirty);
            Assert.True(viewModel.ToSettings().KeepAwake);
            viewModel.MarkAsSaved();
            viewModel.KeepAwake = false;
            viewModel.Revert();
            Assert.True(viewModel.KeepAwake);
            Assert.False(viewModel.IsDirty);
        }

        [Fact]
        public void AudioPlaybackSetting_IsSavedTrackedAsDirtyAndReverted()
        {
            var viewModel = new MonitorConfigurationViewModel(new MonitorInfo
            {
                HardwareId = "MON-1"
            });

            viewModel.ApplySettings(new MonitorSettings { IsActiveOnAudioPlayback = true });

            Assert.True(viewModel.ToSettings().IsActiveOnAudioPlayback);

            viewModel.MarkAsSaved();
            viewModel.IsActiveOnAudioPlayback = false;

            Assert.True(viewModel.IsDirty);
            viewModel.Revert();
            Assert.True(viewModel.IsActiveOnAudioPlayback);
            Assert.False(viewModel.IsDirty);
        }
    }
}
