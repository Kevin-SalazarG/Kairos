using Kairos.Settings;

namespace Kairos.Engine;

public enum EncoderVendor { Nvidia, Amd, Intel, Software }

public sealed record EncoderChoice(string Id, EncoderVendor Vendor, VideoCodec Codec);

/// <summary>Hardware encoders from every GPU vendor libobs supports, and how to configure each one.</summary>
public static class EncoderCatalog
{
    const string X264 = "obs_x264";

    static readonly (VideoCodec Codec, EncoderVendor Vendor, string Id)[] Table =
    [
        (VideoCodec.Av1, EncoderVendor.Nvidia, "obs_nvenc_av1_tex"),
        (VideoCodec.Av1, EncoderVendor.Amd, "av1_texture_amf"),
        (VideoCodec.Av1, EncoderVendor.Intel, "obs_qsv11_av1"),
        (VideoCodec.Hevc, EncoderVendor.Nvidia, "obs_nvenc_hevc_tex"),
        (VideoCodec.Hevc, EncoderVendor.Amd, "h265_texture_amf"),
        (VideoCodec.Hevc, EncoderVendor.Intel, "obs_qsv11_hevc"),
        (VideoCodec.H264, EncoderVendor.Nvidia, "obs_nvenc_h264_tex"),
        (VideoCodec.H264, EncoderVendor.Amd, "h264_texture_amf"),
        (VideoCodec.H264, EncoderVendor.Intel, "obs_qsv11_v2"),
    ];

    /// <summary>Best available encoder: the wanted codec on any vendor, then HEVC, H.264, AV1; x264 last.</summary>
    public static EncoderChoice Pick(VideoCodec wanted, IReadOnlyCollection<string> available)
    {
        foreach (VideoCodec codec in new[] { wanted, VideoCodec.Hevc, VideoCodec.H264, VideoCodec.Av1 }.Distinct())
            foreach (var row in Table.Where(r => r.Codec == codec))
                if (available.Contains(row.Id))
                    return new EncoderChoice(row.Id, row.Vendor, row.Codec);
        return new EncoderChoice(X264, EncoderVendor.Software, VideoCodec.H264);
    }

    /// <summary>Whether a hardware encoder exists for <paramref name="codec"/>.</summary>
    public static bool HasHardware(VideoCodec codec, IReadOnlyCollection<string> available) =>
        Table.Any(r => r.Codec == codec && available.Contains(r.Id));

    /// <summary>CPU encoding is kept to 1080p at 30 fps so it stays usable while gaming.</summary>
    public static ((int W, int H) Size, int Fps) LimitForSoftware((int W, int H) size, int fps)
    {
        var (w, h) = size;
        if (h > 1080)
        {
            w = (int)Math.Round(w * 1080.0 / h) & ~1;
            h = 1080;
        }
        return ((w, h), Math.Min(fps, 30));
    }

    /// <summary>libobs encoder settings for <paramref name="choice"/>, from the quality numbers in <paramref name="plan"/>.</summary>
    public static Dictionary<string, object> Settings(EncoderPlan plan, EncoderChoice choice, VideoSettings? video = null)
    {
        // plan.Cq is on the 1-63 scale for AV1 and the 1-51 scale for HEVC/H.264 (see EncoderPresets).
        long cq = plan.Cq;
        long cq51 = choice.Codec == VideoCodec.Av1 ? (long)Math.Round(plan.Cq * 51.0 / 63.0, MidpointRounding.AwayFromZero) : plan.Cq;
        string profile = choice.Codec == VideoCodec.H264 ? "high" : "main";

        var s = new Dictionary<string, object> { ["keyint_sec"] = (long)plan.KeyintSec };
        switch (choice.Vendor)
        {
            case EncoderVendor.Nvidia:
                s["rate_control"] = "CQVBR";
                s["target_quality"] = cq;
                s["max_bitrate"] = (long)plan.MaxBitrateKbps;
                s["bitrate"] = (long)plan.MaxBitrateKbps;
                s["preset"] = plan.Preset;
                s["tune"] = "hq";
                s["multipass"] = "qres";
                s["profile"] = profile;
                s["bf"] = (long)plan.BFrames;
                s["lookahead"] = false;
                s["adaptive_quantization"] = true;
                break;
            case EncoderVendor.Amd:
                bool constantQp = video?.AmdRateControl == AmdRateControl.Cqp;
                s["rate_control"] = constantQp ? "CQP" : "QVBR";
                s["cqp"] = constantQp ? (long)Math.Clamp(video!.AmdCqp, 1, 51) : cq;
                // OBS AMF ignores bitrate in CQP and QVBR. Do not advertise it as a quality cap.
                s["preset"] = "quality";
                s["pre_analysis"] = false;
                s["profile"] = profile;
                s["bf"] = choice.Codec == VideoCodec.Hevc ? 0L : 2L;
                break;
            case EncoderVendor.Intel:
                s["rate_control"] = "ICQ";
                s["icq_quality"] = cq51;
                s["max_bitrate"] = (long)plan.MaxBitrateKbps;
                s["target_usage"] = "TU4";
                s["profile"] = profile;
                break;
            default:
                s["rate_control"] = "CRF";
                s["crf"] = Math.Max(cq51, 20);
                s["preset"] = "veryfast";
                s["profile"] = "high";
                break;
        }
        return s;
    }
}
