namespace Kairos.Engine.Updates;

/// <summary>Where an update stands, as the settings window's bar shows it.</summary>
public enum UpdateState { None, Available, Downloading, Ready, WaitingForRecording, Failed, WaitingForWindow }

/// <param name="NotesUrl">The release's page ("What's new"); null when the updates come from a local folder.</param>
public sealed record UpdateStatus(string Version, UpdateState State, int Percent, string? NotesUrl);

/// <summary>The decisions about updates, apart from the downloading itself.</summary>
public static class UpdateRules
{
    public const string Repository = "https://github.com/Kevin-SalazarG/Kairos";

    /// <summary>The first look waits this long after the replay buffer is up, so a start-up is never slowed by it.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Time to look for an update: turned on, a minute after the replay buffer first ran, then once a day.</summary>
    public static bool ShouldCheck(bool enabled, DateTime? bufferActiveSince, DateTime? lastCheck, DateTime now) =>
        enabled && bufferActiveSince is { } since && now - since >= FirstDelay && (lastCheck is not { } last || now - last >= Interval);

    /// <summary>Each new version gets one notice, ever.</summary>
    public static bool ShouldNotify(string version, IReadOnlySet<string> seen) => !seen.Contains(version);

    /// <summary>
    /// A downloaded update goes in only when nothing is lost: it waits for a recording to end (it never ends one), and for
    /// the settings window to be free (unsaved changes, the editor, sharing).
    /// </summary>
    public static UpdateState WhenDownloaded(bool recording, bool windowBusy) =>
        recording ? UpdateState.WaitingForRecording : windowBusy ? UpdateState.WaitingForWindow : UpdateState.Ready;

    /// <summary>The window says whether it is in use with each status question (twice a second); a closed one stops asking.</summary>
    public static readonly TimeSpan WindowSilence = TimeSpan.FromSeconds(2);

    public static bool WindowBusy(bool saidBusy, DateTime askedAt, DateTime now) => saidBusy && now - askedAt < WindowSilence;

    public static string? NotesUrl(string version, bool localSource) => localSource ? null : $"{Repository}/releases/tag/v{version}";

    /// <summary>The local update folder named in Kairos's settings folder (testing before publishing), if it exists.</summary>
    public static string? LocalFeed(string appDataDir) => LocalSource(Path.Combine(appDataDir, "update-source.txt"));

    /// <summary>The local folder named in <paramref name="overrideFile"/> (testing before publishing), if it exists.</summary>
    public static string? LocalSource(string overrideFile)
    {
        try
        {
            return File.Exists(overrideFile) && File.ReadAllText(overrideFile).Trim() is { Length: > 0 } folder && Directory.Exists(folder)
                ? folder
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
