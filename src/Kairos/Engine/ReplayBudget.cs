using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Packet-memory limits independent of the encoder's quality target.</summary>
public static class ReplayBudget
{
    public static int LimitMb(AppSettings settings, EncoderChoice choice, EncoderPlan plan, int seconds, long physicalBytes)
    {
        if (choice.Vendor != EncoderVendor.Amd)
            return BufferBudget.CapMb(EncoderPresets.EstimateBufferMb(plan.MaxBitrateKbps, seconds), physicalBytes);

        // Neither AMD quality mode has a bitrate ceiling. Bound memory instead of reducing image quality.
        // When full, libobs discards the oldest GOPs, so a complex scene can shorten a saved replay.
        int total = Math.Clamp(settings.Replay.MaxMemoryMb, 128, 4096);
        if (physicalBytes > 0)
            total = Math.Min(total, (int)Math.Max(128, physicalBytes / (4L * 1024 * 1024)));
        return settings.Replay.QuickEnabled ? total / 2 : total;
    }
}
