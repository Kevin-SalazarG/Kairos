using Kairos.Engine.Share;

namespace Kairos.Settings;

public enum CaptureMode { Screen, Window, Game }

public enum VideoCodec { Av1, Hevc, H264 }

public enum QualityPreset { Ultra, High, Balanced, Light, Custom }

public enum AmdRateControl { Qvbr, Cqp }

public enum AmdEncoderPreset { Quality, Balanced, Speed }

public enum OutputResolution { Native, P2160, P1440, P1080, P720 }

public enum VoiceSensitivity { Strict, Normal, Easy }

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public CaptureSettings Capture { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public ReplaySettings Replay { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public ShareSettings Share { get; set; } = new();

    /// <summary>
    /// Windows' own language when Kairos speaks it: the culture or one it falls back to (zh-CN is zh-Hans, es-MX is
    /// es), else the same language in another region (Portugal's Portuguese is Brazil's), never across scripts
    /// (Traditional Chinese is not Simplified); otherwise English.
    /// </summary>
    public static string DefaultLanguage(System.Globalization.CultureInfo uiCulture)
    {
        for (var culture = uiCulture; culture.Name.Length > 0; culture = culture.Parent)
            if (App.Text.IsSupported(culture.Name))
                return culture.Name;
        var regional = App.Text.Languages.FirstOrDefault(l =>
            l.Code.Split('-') is [var language, { Length: 2 }] && language == uiCulture.TwoLetterISOLanguageName);
        return regional?.Code ?? App.Text.Fallback;
    }

    /// <summary>Clamps every value into its supported range and replaces missing sections.</summary>
    public void Normalize()
    {
        Capture ??= new();
        Video ??= new();
        Replay ??= new();
        Audio ??= new();
        Hotkeys ??= new();
        Output ??= new();
        General ??= new();
        Voice ??= new();
        Share ??= new();

        Capture.MonitorId ??= "";
        Capture.Window ??= "";

        Video.Fps = Math.Clamp(Video.Fps, 10, 240);
        Video.CustomCq = Math.Clamp(Video.CustomCq, 1, 63);
        Video.CustomMbps = Math.Clamp(Video.CustomMbps, 2, 300);
        if (string.IsNullOrWhiteSpace(Video.Preset)) Video.Preset = "p5";
        if (!Enum.IsDefined(Video.AmdRateControl)) Video.AmdRateControl = AmdRateControl.Qvbr;
        Video.AmdCqp = Math.Clamp(Video.AmdCqp, 1, Video.Codec == VideoCodec.Av1 ? 63 : 51);
        if (!Enum.IsDefined(Video.AmdPreset)) Video.AmdPreset = AmdEncoderPreset.Quality;

        Replay.Seconds = Math.Clamp(Replay.Seconds, 15, 1200);
        Replay.QuickSeconds = Math.Clamp(Replay.QuickSeconds, 5, 120);
        Replay.MaxMemoryMb = Math.Clamp(Replay.MaxMemoryMb, 128, 4096);

        Audio.SystemVolume = Math.Clamp(Audio.SystemVolume, 0, 2);
        Audio.MicVolume = Math.Clamp(Audio.MicVolume, 0, 2);
        Audio.BitrateKbps = Math.Clamp(Audio.BitrateKbps, 64, 320);
        if (string.IsNullOrWhiteSpace(Audio.SystemDevice)) Audio.SystemDevice = "default";
        if (string.IsNullOrWhiteSpace(Audio.MicDevice)) Audio.MicDevice = "default";

        Hotkeys.Save ??= "";
        Hotkeys.Quick ??= "";
        Hotkeys.ToggleBuffer ??= "";
        Hotkeys.Record ??= "";
        Hotkeys.PauseRecord ??= App.HotkeyParser.WithShift(Hotkeys.Record);

        // A relative folder would resolve inside the program folder and be lost when the app is replaced.
        if (string.IsNullOrWhiteSpace(Output.Folder) || !Path.IsPathFullyQualified(Output.Folder)) Output.Folder = OutputSettings.DefaultFolder;
        Output.MaxFolderGb = Math.Clamp(Output.MaxFolderGb, 0, 100_000);

        if (!App.Text.IsSupported(General.Language)) General.Language = App.Text.Fallback;
        if (!Enum.IsDefined(Voice.Sensitivity)) Voice.Sensitivity = VoiceSensitivity.Normal;

        Share.CropPosition = double.IsFinite(Share.CropPosition) ? Math.Clamp(Share.CropPosition, 0, 1) : 0.5;
        Share.Tags ??= "";
        Share.PreviewVolume = double.IsFinite(Share.PreviewVolume) && Share.PreviewVolume is >= 0 and <= 1 ? Share.PreviewVolume : 1;
        Share.VerticalFor = (Share.VerticalFor ?? []).Where(p => Enum.IsDefined(p)).Distinct().ToList();
    }
}

public sealed class CaptureSettings
{
    public CaptureMode Mode { get; set; } = CaptureMode.Screen;
    /// <summary>libobs monitor id; empty means the primary monitor.</summary>
    public string MonitorId { get; set; } = "";
    /// <summary>libobs window string "title:class:exe" used by Window mode.</summary>
    public string Window { get; set; } = "";
    public bool GameFallbackToScreen { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
}

public sealed class VideoSettings
{
    public VideoCodec Codec { get; set; } = VideoCodec.Av1;
    public int Fps { get; set; } = 60;
    public OutputResolution Resolution { get; set; } = OutputResolution.P1440;
    public QualityPreset Quality { get; set; } = QualityPreset.Ultra;
    public int CustomCq { get; set; } = 22;
    public int CustomMbps { get; set; } = 40;
    public string Preset { get; set; } = "p5";
    // Keep old settings on QVBR; CQP uses the codec's native QP scale, without AV1 conversion.
    public AmdRateControl AmdRateControl { get; set; } = AmdRateControl.Qvbr;
    public int AmdCqp { get; set; } = 16;
    public AmdEncoderPreset AmdPreset { get; set; } = AmdEncoderPreset.Quality;
}

public sealed class ReplaySettings
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 300;
    public bool QuickEnabled { get; set; }
    public int QuickSeconds { get; set; } = 30;
    /// <summary>Total encoded-packet budget for AMD quality modes; they do not enforce a bitrate ceiling.</summary>
    public int MaxMemoryMb { get; set; } = 1024;
}

public sealed class AudioSettings
{
    public bool System { get; set; } = true;
    public string SystemDevice { get; set; } = "default";
    public double SystemVolume { get; set; } = 1.0;
    public bool Mic { get; set; } = true;
    public string MicDevice { get; set; } = "default";
    public double MicVolume { get; set; } = 1.0;
    public bool SeparateTracks { get; set; } = true;
    /// <summary>Window mode: only the captured app's audio. Game mode: only the game's audio.</summary>
    public bool SourceAudioOnly { get; set; }
    public int BitrateKbps { get; set; } = 192;

    /// <summary>Picking a microphone means the user wants it recorded.</summary>
    public void SelectMic(string deviceId)
    {
        MicDevice = deviceId;
        Mic = true;
    }
}

public sealed class HotkeySettings
{
    public string Save { get; set; } = "Alt+F10";
    public string Quick { get; set; } = "";
    public string ToggleBuffer { get; set; } = "Alt+Shift+F10";
    public string Record { get; set; } = "Alt+F9";
    /// <summary>Pauses and resumes a full recording. Missing (settings from before it existed): the recording keys plus Shift.</summary>
    public string? PauseRecord { get; set; }
    /// <summary>Takes a screenshot. Alt+F12: free of OBS's F9–F11 and RTSS's Ctrl+F12.</summary>
    public string Screenshot { get; set; } = "Alt+F12";
}

public sealed class OutputSettings
{
    public static readonly string DefaultFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), AppInfo.Name);

    public string Folder { get; set; } = DefaultFolder;
    public bool PerAppFolders { get; set; } = true;
    /// <summary>0 disables the clean-up of old clips.</summary>
    public int MaxFolderGb { get; set; }
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool SoundOnSave { get; set; } = true;
    public bool ToastOnSave { get; set; } = true;
    public bool OnlyWhenGaming { get; set; }
    public string Language { get; set; } = "ar";
    /// <summary>Look for a newer Kairos once a day (off: Kairos never connects for it).</summary>
    public bool CheckUpdates { get; set; } = true;
}

/// <summary>Smart Save: save the replay when the user says their recorded phrase.</summary>
public sealed class VoiceSettings
{
    public bool Enabled { get; set; }
    public VoiceSensitivity Sensitivity { get; set; } = VoiceSensitivity.Normal;
    /// <summary>The status-page card that introduces Smart Save was closed.</summary>
    public bool HintDismissed { get; set; }

    /// <summary>
    /// Normal talk scored 0.114 and up against a real user's phrase while their attempts scored 0.05-0.08,
    /// so no threshold goes past 0.100, keeping a margin against saving on ordinary conversation.
    /// </summary>
    public const double MaxThreshold = 0.100;

    /// <summary>
    /// Highest phrase-matching cost that still counts as the phrase. <paramref name="spread"/> is how far the
    /// user's own samples are from each other (<see cref="Engine.Voice.VoicePhrase.Spread"/>): a real voice varies
    /// more than the synthetic voices the floors came from, so the threshold grows with it.
    /// </summary>
    public static double Threshold(VoiceSensitivity s, double spread = 0)
    {
        (double floor, double factor) = s switch
        {
            VoiceSensitivity.Strict => (0.052, 1.5),
            VoiceSensitivity.Easy => (0.066, 2.1),
            _ => (0.060, 1.8),
        };
        return Math.Min(MaxThreshold, Math.Max(floor, factor * spread));
    }
}

/// <summary>The share window's last choices, so the next share starts where the user left off.</summary>
public sealed class ShareSettings
{
    /// <summary>The signed-in X account has Premium (seen on its page while posting); null until known.</summary>
    public bool? XPremium { get; set; }
    /// <summary>Platforms the user turned the 9:16 cut on for (off for every platform at first).</summary>
    public List<SharePlatform> VerticalFor { get; set; } = [];
    /// <summary>Where the 9:16 window sits: 0 = left edge, 1 = right edge.</summary>
    public double CropPosition { get; set; } = 0.5;
    /// <summary>The user's own hashtags, added after the game's tag.</summary>
    public string Tags { get; set; } = "#gaming";
    /// <summary>Loudness of the share panel's preview (0..1); the shared clip keeps its own sound.</summary>
    public double PreviewVolume { get; set; } = 1;
    public bool PreviewMuted { get; set; }

    public ShareSettings Copy()
    {
        var copy = (ShareSettings)MemberwiseClone();
        copy.VerticalFor = [.. VerticalFor];
        return copy;
    }
}
