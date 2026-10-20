using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

public static class MotionQualityTests
{
    static readonly EncoderChoice Amd = new("h265_texture_amf", EncoderVendor.Amd, VideoCodec.Hevc);
    static EncoderPlan Plan => EncoderPresets.Plan(VideoCodec.Hevc, QualityPreset.Custom, 1920, 1080, 60, 20, 60, true);

    [Test]
    public static void Cqp_uses_native_qp_and_does_not_apply_a_bitrate_cap()
    {
        var video = new VideoSettings { AmdRateControl = AmdRateControl.Cqp, AmdCqp = 16 };
        var actual = EncoderCatalog.Settings(Plan, Amd, video);
        Assert.Equal("CQP", (string)actual["rate_control"]);
        Assert.Equal(16L, (long)actual["cqp"]);
        Assert.True(!actual.ContainsKey("bitrate"), "CQP must not pretend to enforce a bitrate ceiling");
        Assert.Equal(0L, (long)actual["bf"], "HEVC has no AMF B-frame control");
    }

    [Test]
    public static void Old_settings_keep_qvbr()
    {
        var old = SettingsStore.Deserialize("{\"video\":{\"codec\":\"Hevc\"}}");
        Assert.Equal(AmdRateControl.Qvbr, old.Video.AmdRateControl);
        Assert.Equal("QVBR", (string)EncoderCatalog.Settings(Plan, Amd, old.Video)["rate_control"]);
    }

    [Test]
    public static void Amd_memory_budget_is_independent_of_nominal_bitrate()
    {
        var s = new AppSettings();
        s.Replay.MaxMemoryMb = 1024;
        Assert.Equal(1024, ReplayBudget.LimitMb(s, Amd, Plan, 120, 16L << 30));
        Assert.Equal(1024, ReplayBudget.LimitMb(s, Amd, Plan with { MaxBitrateKbps = 4000 }, 120, 16L << 30));
        s.Replay.QuickEnabled = true;
        Assert.Equal(512, ReplayBudget.LimitMb(s, Amd, Plan, 120, 16L << 30), "quick and regular share the budget");
    }

    [Test]
    public static void Amd_memory_budget_respects_small_machines_and_invalid_settings()
    {
        var s = new AppSettings();
        s.Replay.MaxMemoryMb = 4096;
        Assert.Equal(512, ReplayBudget.LimitMb(s, Amd, Plan, 120, 2L << 30));
        s.Replay.MaxMemoryMb = -1;
        s.Video.AmdCqp = 999;
        s.Video.AmdRateControl = (AmdRateControl)99;
        s.Normalize();
        Assert.Equal(128, s.Replay.MaxMemoryMb);
        Assert.Equal(51, s.Video.AmdCqp);
        Assert.Equal(AmdRateControl.Qvbr, s.Video.AmdRateControl);
    }

    [Test]
    public static void Amd_quality_changes_require_a_pipeline_rebuild()
    {
        var before = new AppSettings();
        var after = SettingsStore.Deserialize(SettingsStore.Serialize(before));
        after.Video.AmdRateControl = AmdRateControl.Cqp;
        Assert.True(SettingsDiff.NeedsRebuild(before, after), "rate control must reach the live encoder");
        after = SettingsStore.Deserialize(SettingsStore.Serialize(before));
        after.Replay.MaxMemoryMb = 512;
        Assert.True(SettingsDiff.NeedsRebuild(before, after), "packet limit belongs to the pipeline");
    }

    [Test]
    public static void Amd_options_do_not_change_other_vendors()
    {
        var s = EncoderCatalog.Settings(Plan, new("obs_nvenc_hevc_tex", EncoderVendor.Nvidia, VideoCodec.Hevc),
            new VideoSettings { AmdRateControl = AmdRateControl.Cqp, AmdCqp = 10 });
        Assert.Equal("CQVBR", (string)s["rate_control"]);
        Assert.Equal((long)Plan.Cq, (long)s["target_quality"]);
    }
}
