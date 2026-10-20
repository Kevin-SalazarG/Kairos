using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

public static class AmdControlTests
{
    static EncoderChoice Amd(VideoCodec codec) => new(codec switch
    {
        VideoCodec.Av1 => "av1_texture_amf",
        VideoCodec.Hevc => "h265_texture_amf",
        _ => "h264_texture_amf",
    }, EncoderVendor.Amd, codec);

    static EncoderPlan Plan(VideoCodec codec) =>
        EncoderPresets.Plan(codec, QualityPreset.Custom, 1920, 1080, 60, 20, 60, true, "p7");

    [Test]
    public static void Amd_speed_choices_reach_the_native_encoder_without_pre_analysis()
    {
        foreach (var (preset, expected) in new[]
        {
            (AmdEncoderPreset.Speed, "speed"),
            (AmdEncoderPreset.Balanced, "balanced"),
            (AmdEncoderPreset.Quality, "quality"),
        })
        {
            var video = new VideoSettings { AmdPreset = preset, Preset = "p7" };
            var actual = EncoderCatalog.Settings(Plan(VideoCodec.Hevc), Amd(VideoCodec.Hevc), video);
            Assert.Equal(expected, (string)actual["preset"], preset.ToString());
            Assert.Equal(false, (bool)actual["pre_analysis"], "analysis must remain off for gaming");
        }
    }

    [Test]
    public static void Amd_settings_normalize_native_qp_per_codec_and_repair_invalid_presets()
    {
        foreach (var (codec, upperBound) in new[]
        {
            (VideoCodec.Av1, 63),
            (VideoCodec.Hevc, 51),
            (VideoCodec.H264, 51),
        })
        {
            var settings = new AppSettings();
            settings.Video.Codec = codec;
            settings.Video.AmdPreset = (AmdEncoderPreset)999;
            settings.Video.AmdCqp = 999;
            settings.Normalize();
            Assert.Equal(AmdEncoderPreset.Quality, settings.Video.AmdPreset, "invalid preset");
            Assert.Equal(upperBound, settings.Video.AmdCqp, codec.ToString());
            settings.Video.AmdCqp = -1;
            settings.Normalize();
            Assert.Equal(1, settings.Video.AmdCqp, "invalid lower bound");
        }
    }

    [Test]
    public static void Amd_mode_changes_preserve_saved_native_qp_and_speed()
    {
        var settings = new AppSettings();
        settings.Video.Codec = VideoCodec.Hevc;
        settings.Video.AmdRateControl = AmdRateControl.Cqp;
        settings.Video.AmdCqp = 17;
        settings.Video.AmdPreset = AmdEncoderPreset.Balanced;

        settings.Video.AmdRateControl = AmdRateControl.Qvbr;
        var saved = SettingsStore.Deserialize(SettingsStore.Serialize(settings));
        var qvbr = EncoderCatalog.Settings(Plan(VideoCodec.Hevc), Amd(VideoCodec.Hevc), saved.Video);
        Assert.Equal("QVBR", (string)qvbr["rate_control"]);
        Assert.Equal(17, saved.Video.AmdCqp, "native QP survives QVBR and persistence");

        saved.Video.AmdRateControl = AmdRateControl.Cqp;
        var cqp = EncoderCatalog.Settings(Plan(VideoCodec.Hevc), Amd(VideoCodec.Hevc), saved.Video);
        Assert.Equal("CQP", (string)cqp["rate_control"]);
        Assert.Equal(17L, (long)cqp["cqp"], "returning to CQP restores the native target");
        Assert.Equal("balanced", (string)cqp["preset"]);
    }

    [Test]
    public static void Amd_av1_can_use_native_qp_63_without_hevc_scaling()
    {
        var video = new VideoSettings
        {
            Codec = VideoCodec.Hevc,
            AmdRateControl = AmdRateControl.Cqp,
            AmdCqp = 63,
        };
        var actual = EncoderCatalog.Settings(Plan(VideoCodec.Av1), Amd(VideoCodec.Av1), video);
        Assert.Equal(63L, (long)actual["cqp"], "the effective encoder determines the native range");
    }

    [Test]
    public static void Amd_hevc_and_h264_fallbacks_clamp_qp_to_the_effective_codec()
    {
        var video = new VideoSettings
        {
            Codec = VideoCodec.Av1,
            AmdRateControl = AmdRateControl.Cqp,
            AmdCqp = 63,
        };
        foreach (VideoCodec codec in new[] { VideoCodec.Hevc, VideoCodec.H264 })
        {
            var actual = EncoderCatalog.Settings(Plan(codec), Amd(codec), video);
            Assert.Equal(51L, (long)actual["cqp"], codec.ToString());
        }
    }

    [Test]
    public static void Amd_speed_changes_leave_nvidia_presets_unchanged()
    {
        var nvidia = new EncoderChoice("obs_nvenc_hevc_tex", EncoderVendor.Nvidia, VideoCodec.Hevc);
        foreach (string preset in new[] { "p1", "p4", "p7" })
        {
            var video = new VideoSettings
            {
                AmdPreset = AmdEncoderPreset.Speed,
                AmdRateControl = AmdRateControl.Cqp,
                AmdCqp = 10,
                Preset = preset,
            };
            var actual = EncoderCatalog.Settings(Plan(VideoCodec.Hevc) with { Preset = preset }, nvidia, video);
            Assert.Equal(preset, (string)actual["preset"]);
            Assert.Equal("CQVBR", (string)actual["rate_control"]);
            Assert.True(!actual.ContainsKey("pre_analysis"), "AMD options must not reach NVENC");
        }
    }

    [Test]
    public static void Legacy_amd_settings_keep_quality_and_new_speed_requires_rebuild()
    {
        var legacy = SettingsStore.Deserialize("{\"video\":{\"codec\":\"Hevc\"}}");
        Assert.Equal(AmdEncoderPreset.Quality, legacy.Video.AmdPreset, "legacy default");
        var actual = EncoderCatalog.Settings(Plan(VideoCodec.Hevc), Amd(VideoCodec.Hevc), legacy.Video);
        Assert.Equal("quality", (string)actual["preset"]);

        var changed = SettingsStore.Deserialize(SettingsStore.Serialize(legacy));
        changed.Video.AmdPreset = AmdEncoderPreset.Balanced;
        Assert.True(SettingsDiff.NeedsRebuild(legacy, changed), "speed changes must rebuild the encoder");
    }

    [Test]
    public static void Unknown_persisted_amd_presets_fall_back_to_quality()
    {
        foreach (string json in new[]
        {
            "{\"video\":{\"codec\":\"Hevc\",\"amdPreset\":\"future-preset\"}}",
            "{\"video\":{\"codec\":\"Hevc\",\"amdPreset\":999}}",
        })
        {
            var settings = SettingsStore.Deserialize(json);
            Assert.Equal(AmdEncoderPreset.Quality, settings.Video.AmdPreset, "unknown persisted preset");
            var actual = EncoderCatalog.Settings(Plan(VideoCodec.Hevc), Amd(VideoCodec.Hevc), settings.Video);
            Assert.Equal("quality", (string)actual["preset"], "invalid JSON must not select Speed");
        }
    }
}
