using Kairos.App;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

public static class AmdViewModelTests
{
    static CapsDto Caps(params string[] encoders) => new([], [], [], [], [.. encoders], "32.2.2");

    [Test]
    public static void Profile_command_requires_amd_hevc_and_updates_effective_controls()
    {
        var vm = new SettingsViewModel();
        vm.Seed(null, Caps("obs_nvenc_hevc_tex"));
        Assert.True(!vm.Rx6600ProfileCommand.CanExecute(null), "profile must not configure NVIDIA as AMD");
        vm.Seed(null, Caps("h265_texture_amf"));
        Assert.True(vm.Rx6600ProfileCommand.CanExecute(null), "AMD HEVC supports the profile");
        vm.Rx6600ProfileCommand.Execute(null);
        Assert.True(vm.AmdControlsVisible && vm.AmdCqpVisible, "native AMD controls must be visible");
        Assert.True(!vm.LegacyQualityVisible && !vm.BitrateControlVisible && !vm.NvencSpeedVisible,
            "irrelevant quality, bitrate and NVIDIA controls must be hidden");
        Assert.Equal(16, vm.AmdQp);
        Assert.Equal(AmdEncoderPreset.Balanced, vm.AmdSpeed);
        Assert.Equal(1024, vm.ReplayMemoryMb);
        Assert.Equal(60, vm.Fps);
        Assert.True(vm.IsDirty, "the normal Apply action must save the profile");
    }

    [Test]
    public static void Switching_amd_modes_keeps_qp_and_exposes_only_the_relevant_quality_control()
    {
        var vm = new SettingsViewModel();
        vm.Seed(null, Caps("h265_texture_amf"));
        vm.Codec = VideoCodec.Hevc;
        vm.AmdMode = AmdRateControl.Cqp;
        vm.AmdQp = 17;
        vm.AmdMode = AmdRateControl.Qvbr;
        Assert.True(vm.LegacyQualityVisible && !vm.AmdCqpVisible, "QVBR uses the normal quality preset");
        Assert.True(!vm.BitrateControlVisible, "QVBR does not enforce a bitrate cap");
        vm.AmdMode = AmdRateControl.Cqp;
        Assert.Equal(17, vm.AmdQp);
        Assert.True(!vm.LegacyQualityVisible && vm.AmdCqpVisible, "CQP uses native QP");
    }

    [Test]
    public static void Unsupported_av1_uses_the_effective_hevc_qp_range_and_display()
    {
        var vm = new SettingsViewModel();
        vm.Seed(null, Caps("av1_texture_amf", "h265_texture_amf"));
        vm.Codec = VideoCodec.Av1;
        vm.AmdMode = AmdRateControl.Cqp;
        vm.AmdQp = 63;
        Assert.Equal(63, vm.AmdQpMaximum, "native AV1 range");
        Assert.Equal(63, vm.AmdQp, "saved AV1 target");

        vm.Seed(null, Caps("h265_texture_amf"));
        Assert.Equal(VideoCodec.Av1, vm.Codec, "the saved codec can remain unsupported");
        Assert.True(vm.AmdCqpVisible, "HEVC fallback still uses AMD controls");
        Assert.Equal(51, vm.AmdQpMaximum, "the slider must follow the effective HEVC encoder");
        Assert.Equal(51, vm.AmdQp, "the displayed value must match the effective encoder clamp");

        vm.AmdQp = 63;
        vm.Seed(null, Caps("av1_texture_amf", "h265_texture_amf"));
        Assert.Equal(51, vm.AmdQp, "edits while using HEVC must respect its native range");
    }
}
