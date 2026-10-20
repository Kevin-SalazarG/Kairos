namespace Kairos.Settings;

/// <summary>Explicit capture profiles that preserve the user's devices, output, and preferences.</summary>
public static class CaptureProfiles
{
    /// <summary>
    /// Requires the AMD HEVC texture encoder. Encoder availability identifies a capability, not an adapter model.
    /// </summary>
    public static bool IsRx6600Compatible(IReadOnlyCollection<string> encoderIds) =>
        encoderIds.Contains("h265_texture_amf", StringComparer.Ordinal);

    /// <summary>
    /// Applies a starting point for 1080p60 game capture on an RX 6600 with 16 GB RAM.
    /// A low native QP targets motion detail with variable file size; the packet budget may limit replay duration.
    /// The caller checks encoder availability; the normal Apply action saves and rebuilds the pipeline.
    /// </summary>
    public static void ApplyRx6600(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Capture ??= new();
        settings.Video ??= new();
        settings.Replay ??= new();
        settings.General ??= new();

        settings.Capture.Mode = CaptureMode.Game;
        settings.Capture.GameFallbackToScreen = false;

        settings.Video.Codec = VideoCodec.Hevc;
        settings.Video.Resolution = OutputResolution.P1080;
        settings.Video.Fps = 60;
        settings.Video.Quality = QualityPreset.Custom;
        settings.Video.CustomCq = 20;
        settings.Video.CustomMbps = 60;
        settings.Video.Preset = "p4";
        settings.Video.AmdRateControl = AmdRateControl.Cqp;
        settings.Video.AmdCqp = 16;
        settings.Video.AmdPreset = AmdEncoderPreset.Balanced;

        settings.Replay.Enabled = true;
        settings.Replay.Seconds = 120;
        settings.Replay.MaxMemoryMb = 1024;
        settings.Replay.QuickEnabled = false;
        settings.General.OnlyWhenGaming = true;
    }

    /// <summary>
    /// Explicit QP 14 experiment for comparison with the stable QP 16 profile.
    /// Keeps the same replay packet budget; larger frames may shorten the available replay.
    /// </summary>
    public static void ApplyRx6600DetailTrial(AppSettings settings)
    {
        ApplyRx6600(settings);
        settings.Video.AmdCqp = 14;
    }
}
