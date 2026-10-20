namespace Kairos.Engine.Voice;

/// <summary>What a Smart Save phrase does: the first saves a clip, the second takes a screenshot.</summary>
public enum VoiceAction { Clip, Shot }

/// <summary>Which phrase was said, with two of them listening at once.</summary>
public static class VoiceRouting
{
    /// <summary>Require a tenth of a threshold between two competing actions, instead of guessing.</summary>
    public const double AmbiguityMargin = 0.10;

    public static bool Ambiguous(double? clipScore, double clipThreshold, double? shotScore, double shotThreshold) =>
        clipScore is { } c && shotScore is { } s && clipThreshold > 0 && shotThreshold > 0
        && c < clipThreshold && s < shotThreshold && Math.Abs(c / clipThreshold - s / shotThreshold) < AmbiguityMargin;
    /// <summary>
    /// The phrase heard: of those under their own threshold, the closer one for its threshold (a score of 0.04 against
    /// 0.05 is nearer than 0.05 against 0.06). Null when neither is under. Never both: one utterance, one action.
    /// </summary>
    public static VoiceAction? Pick(double? clipScore, double clipThreshold, double? shotScore, double shotThreshold)
    {
        if (Ambiguous(clipScore, clipThreshold, shotScore, shotThreshold))
            return null;
        double clip = clipScore is { } c && c < clipThreshold ? c / clipThreshold : double.MaxValue;
        double shot = shotScore is { } s && s < shotThreshold ? s / shotThreshold : double.MaxValue;
        if (clip == double.MaxValue && shot == double.MaxValue)
            return null;
        return shot < clip ? VoiceAction.Shot : VoiceAction.Clip;
    }

    /// <summary>A sample for one phrase that the other phrase matches even at Easy could not be told apart from it.</summary>
    public static bool TooClose(double crossScore, double easyThreshold) => crossScore < easyThreshold;

    /// <summary>Each action has its own cooldown, so a screenshot right after a clip still happens.</summary>
    public static bool Ready(DateTime last, DateTime now) => now - last >= VoiceTrigger.Cooldown;
}
