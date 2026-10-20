using Kairos.Settings;

namespace Kairos.Tests;

public static class SettingsTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Test]
    public static void Defaults_match_spec()
    {
        var s = new AppSettings();
        Assert.Equal(CaptureMode.Screen, s.Capture.Mode, "mode");
        Assert.Equal(300, s.Replay.Seconds, "buffer seconds");
        Assert.Equal(OutputResolution.P1440, s.Video.Resolution, "resolution");
        Assert.Equal(60, s.Video.Fps, "fps");
        Assert.Equal(VideoCodec.Av1, s.Video.Codec, "codec");
        Assert.Equal(QualityPreset.Ultra, s.Video.Quality, "quality");
        Assert.Equal(true, s.Audio.System, "system audio");
        Assert.Equal(true, s.Audio.Mic, "mic on");
        Assert.Equal("default", s.Audio.MicDevice, "windows default mic");
        Assert.Equal("default", s.Audio.SystemDevice, "windows default output");
        Assert.Equal(true, s.Audio.SeparateTracks, "separate tracks");
        Assert.Equal("Alt+F10", s.Hotkeys.Save, "save hotkey");
        Assert.Equal("Alt+Shift+F10", s.Hotkeys.ToggleBuffer, "toggle hotkey");
        Assert.Equal("Alt+F9", s.Hotkeys.Record, "record hotkey");
        Assert.Equal(false, s.Replay.QuickEnabled, "quick clip");
        Assert.Equal(true, s.General.StartWithWindows, "autostart");
        Assert.Equal(true, s.General.SoundOnSave, "sound");
        Assert.Equal(true, s.General.ToastOnSave, "toast");
        Assert.Equal("ar", s.General.Language, "language");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), AppInfo.Name), s.Output.Folder, "clips folder");
    }

    [Test]
    public static void First_run_language_follows_windows()
    {
        Assert.Equal("ar", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("ar-SA")), "arabic");
        Assert.Equal("ar", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("ar-EG")), "arabic egypt");
        Assert.Equal("en", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("en-US")), "english");
        Assert.Equal("en", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("fr-FR")), "other");
    }

    [Test]
    public static void Save_then_load_round_trips()
    {
        string path = TempFile();
        var s = new AppSettings();
        s.Capture.Mode = CaptureMode.Game;
        s.Video.Codec = VideoCodec.Hevc;
        s.Replay.Seconds = 120;
        s.Hotkeys.Save = "Alt+F7";
        s.Output.Folder = @"E:\Clips";
        SettingsStore.Save(path, s);

        var loaded = SettingsStore.Load(path);
        Assert.Equal(CaptureMode.Game, loaded.Capture.Mode, "mode");
        Assert.Equal(VideoCodec.Hevc, loaded.Video.Codec, "codec");
        Assert.Equal(120, loaded.Replay.Seconds, "seconds");
        Assert.Equal("Alt+F7", loaded.Hotkeys.Save, "hotkey");
        Assert.Equal(@"E:\Clips", loaded.Output.Folder, "folder");
    }

    [Test]
    public static void Missing_file_gives_defaults()
    {
        var loaded = SettingsStore.Load(TempFile());
        Assert.Equal(300, loaded.Replay.Seconds, "seconds");
    }

    [Test]
    public static void Corrupt_json_gives_defaults_and_keeps_backup()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var loaded = SettingsStore.Load(path);
        Assert.Equal(300, loaded.Replay.Seconds, "seconds");
        string backup = Path.Combine(Path.GetDirectoryName(path)!, "settings.bad.json");
        Assert.True(File.Exists(backup), "settings.bad.json should exist");
        Assert.Equal("{ not json", File.ReadAllText(backup), "backup content");
    }

    [Test]
    public static void Buffer_seconds_are_clamped()
    {
        string path = TempFile();
        var s = new AppSettings();
        s.Replay.Seconds = 5;
        SettingsStore.Save(path, s);
        Assert.Equal(15, SettingsStore.Load(path).Replay.Seconds, "low clamp");

        s.Replay.Seconds = 99999;
        SettingsStore.Save(path, s);
        Assert.Equal(1200, SettingsStore.Load(path).Replay.Seconds, "high clamp");
    }

    [Test]
    public static void Unknown_enum_value_falls_back_to_default_and_keeps_other_fields()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "video": { "codec": "vp9", "fps": 30 } }""");

        var loaded = SettingsStore.Load(path);
        Assert.Equal(VideoCodec.Av1, loaded.Video.Codec, "codec");
        Assert.Equal(30, loaded.Video.Fps, "fps");
    }
}
