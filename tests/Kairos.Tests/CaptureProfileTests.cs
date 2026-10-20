using System.Text.Json;
using Kairos.Settings;

namespace Kairos.Tests;

public static class CaptureProfileTests
{
    [Test]
    public static void Rx6600_profile_sets_motion_quality_and_replay_limits()
    {
        var settings = new AppSettings();
        settings.Replay.Enabled = false;
        settings.Replay.QuickEnabled = true;

        CaptureProfiles.ApplyRx6600(settings);

        Assert.Equal(CaptureMode.Game, settings.Capture.Mode, "game capture");
        Assert.Equal(false, settings.Capture.GameFallbackToScreen, "no redundant display capture");
        Assert.Equal(VideoCodec.Hevc, settings.Video.Codec, "HEVC");
        Assert.Equal(OutputResolution.P1080, settings.Video.Resolution, "resolution");
        Assert.Equal(60, settings.Video.Fps, "frame rate");
        Assert.Equal(QualityPreset.Custom, settings.Video.Quality, "custom quality");
        Assert.Equal(20, settings.Video.CustomCq, "fallback CQ");
        Assert.Equal(60, settings.Video.CustomMbps, "fallback bitrate");
        Assert.Equal("p4", settings.Video.Preset, "encoder speed");
        Assert.Equal(AmdRateControl.Cqp, settings.Video.AmdRateControl, "constant quantizer");
        Assert.Equal(16, settings.Video.AmdCqp, "AMD quantizer");
        Assert.Equal(AmdEncoderPreset.Balanced, settings.Video.AmdPreset, "AMD encoding speed");
        Assert.Equal(true, settings.Replay.Enabled, "replay enabled");
        Assert.Equal(120, settings.Replay.Seconds, "two minutes");
        Assert.Equal(1024, settings.Replay.MaxMemoryMb, "one GiB memory limit");
        Assert.Equal(false, settings.Replay.QuickEnabled, "single replay buffer");
        Assert.Equal(true, settings.General.OnlyWhenGaming, "game-only activation");
    }

    [Test]
    public static void Rx6600_profile_preserves_devices_output_and_preferences() =>
        AssertPersonalSettingsPreserved(CaptureProfiles.ApplyRx6600);

    [Test]
    public static void Rx6600_detail_trial_preserves_devices_output_and_preferences() =>
        AssertPersonalSettingsPreserved(CaptureProfiles.ApplyRx6600DetailTrial);

    static void AssertPersonalSettingsPreserved(Action<AppSettings> applyProfile)
    {
        var settings = new AppSettings
        {
            Version = 7,
            Capture = new CaptureSettings { MonitorId = "monitor-2", Window = "practice:class:game.exe", CaptureCursor = false },
            Audio = new AudioSettings
            {
                System = false, SystemDevice = "speakers", SystemVolume = 0.7,
                Mic = false, MicDevice = "headset", MicVolume = 0.6,
                SeparateTracks = false, SourceAudioOnly = true, BitrateKbps = 320,
            },
            Hotkeys = new HotkeySettings
            {
                Save = "Ctrl+F7", Quick = "Ctrl+F8", ToggleBuffer = "Ctrl+F9",
                Record = "Ctrl+F10", PauseRecord = "Ctrl+Shift+F10", Screenshot = "Ctrl+F12",
            },
            Output = new OutputSettings { Folder = @"E:\Videos\Kairos", PerAppFolders = false, MaxFolderGb = 100 },
            General = new GeneralSettings
            {
                StartWithWindows = false, SoundOnSave = false, ToastOnSave = false,
                Language = "es", CheckUpdates = false,
            },
            Voice = new VoiceSettings { Enabled = true, Sensitivity = VoiceSensitivity.Easy, HintDismissed = true },
            Share = new ShareSettings
            {
                XPremium = true, CropPosition = 0.3, Tags = "#valorant", PreviewVolume = 0.4, PreviewMuted = true,
            },
        };
        settings.Replay.QuickSeconds = 17;
        string audio = JsonSerializer.Serialize(settings.Audio);
        string hotkeys = JsonSerializer.Serialize(settings.Hotkeys);
        string output = JsonSerializer.Serialize(settings.Output);
        string voice = JsonSerializer.Serialize(settings.Voice);
        string share = JsonSerializer.Serialize(settings.Share);
        var audioInstance = settings.Audio;

        applyProfile(settings);

        Assert.Equal(7, settings.Version, "settings version");
        Assert.Equal("monitor-2", settings.Capture.MonitorId, "monitor selection");
        Assert.Equal("practice:class:game.exe", settings.Capture.Window, "window selection");
        Assert.Equal(false, settings.Capture.CaptureCursor, "cursor preference");
        Assert.Equal(17, settings.Replay.QuickSeconds, "stored quick duration");
        Assert.Equal(audio, JsonSerializer.Serialize(settings.Audio), "all audio settings");
        Assert.True(ReferenceEquals(audioInstance, settings.Audio), "audio section must not be replaced");
        Assert.Equal(hotkeys, JsonSerializer.Serialize(settings.Hotkeys), "all hotkeys");
        Assert.Equal(output, JsonSerializer.Serialize(settings.Output), "all output settings");
        Assert.Equal(voice, JsonSerializer.Serialize(settings.Voice), "all voice settings");
        Assert.Equal(share, JsonSerializer.Serialize(settings.Share), "all sharing settings");
        Assert.Equal(false, settings.General.StartWithWindows, "startup preference");
        Assert.Equal(false, settings.General.SoundOnSave, "save sound");
        Assert.Equal(false, settings.General.ToastOnSave, "save notification");
        Assert.Equal("es", settings.General.Language, "language");
        Assert.Equal(false, settings.General.CheckUpdates, "update preference");
    }

    [Test]
    public static void Rx6600_detail_trial_changes_only_qp_from_the_stable_profile()
    {
        var stable = new AppSettings();
        stable.Normalize();
        var trial = SettingsStore.Deserialize(SettingsStore.Serialize(stable));

        CaptureProfiles.ApplyRx6600(stable);
        CaptureProfiles.ApplyRx6600DetailTrial(trial);

        Assert.Equal(16, stable.Video.AmdCqp, "stable profile must remain QP 16");
        Assert.Equal(14, trial.Video.AmdCqp, "explicit trial uses QP 14");
        Assert.Equal(1024, trial.Replay.MaxMemoryMb, "trial keeps the packet budget");
        trial.Video.AmdCqp = stable.Video.AmdCqp;
        Assert.Equal(SettingsStore.Serialize(stable), SettingsStore.Serialize(trial), "all other settings match");
    }

    [Test]
    public static void Rx6600_detail_trial_is_idempotent_and_stable_profile_restores_qp16()
    {
        var settings = new AppSettings();
        CaptureProfiles.ApplyRx6600DetailTrial(settings);
        string firstApplication = SettingsStore.Serialize(settings);
        var applied = SettingsStore.Deserialize(firstApplication);

        CaptureProfiles.ApplyRx6600DetailTrial(settings);

        Assert.Equal(firstApplication, SettingsStore.Serialize(settings), "reapplying trial leaves settings unchanged");
        Assert.Equal(false, SettingsDiff.NeedsRebuild(applied, settings), "unchanged trial keeps the replay buffer");

        CaptureProfiles.ApplyRx6600(settings);

        Assert.Equal(16, settings.Video.AmdCqp, "stable profile reverses the experiment");
        Assert.True(SettingsDiff.NeedsRebuild(applied, settings), "changing quantizer requires encoder rebuild");
    }

    [Test]
    public static void Rx6600_profile_is_idempotent_and_only_rebuilds_on_change()
    {
        var settings = new AppSettings();
        var before = SettingsStore.Deserialize(SettingsStore.Serialize(settings));

        CaptureProfiles.ApplyRx6600(settings);

        Assert.True(SettingsDiff.NeedsRebuild(before, settings), "applying capture settings requires a rebuild");
        string firstApplication = SettingsStore.Serialize(settings);
        var applied = SettingsStore.Deserialize(firstApplication);

        CaptureProfiles.ApplyRx6600(settings);

        Assert.Equal(firstApplication, SettingsStore.Serialize(settings), "reapplying must not change settings");
        Assert.Equal(false, SettingsDiff.NeedsRebuild(applied, settings), "unchanged profile keeps the replay buffer");
    }

    [Test]
    public static void Rx6600_profile_requires_the_amd_hevc_texture_encoder()
    {
        Assert.Equal(true, CaptureProfiles.IsRx6600Compatible(["obs_x264", "h265_texture_amf"]), "AMD HEVC available");
        Assert.Equal(false, CaptureProfiles.IsRx6600Compatible(["h264_texture_amf"]), "H.264 alone is insufficient");
        Assert.Equal(false, CaptureProfiles.IsRx6600Compatible(["jim_hevc_nvenc", "obs_qsv11_hevc"]), "other vendors");
        Assert.Equal(false, CaptureProfiles.IsRx6600Compatible([]), "no encoders");
    }
}
