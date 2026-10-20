using Kairos.App;

namespace Kairos.Tests;

public static class GameActivityTests
{
    static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    sealed class Session(int pid, string path = @"C:\Games\Valorant.exe") : IGameProcessSession
    {
        public int ProcessId => pid;
        public string ExecutablePath => path;
        public bool IsAlive { get; set; } = true;
        public int DisposeCalls;
        public void Dispose() => DisposeCalls++;
    }

    [Test]
    public static void Alt_tab_and_minimize_keep_the_original_game_session()
    {
        var game = new Session(1);
        int opens = 0;
        using var tracker = new GameActivityTracker(_ => { opens++; return game; }, null);
        Assert.True(tracker.Update(1, Start), "fullscreen starts replay");
        Assert.True(tracker.Update(null, Start.AddSeconds(45)), "Alt-Tab beyond the old grace must retain replay");
        Assert.True(tracker.Update(null, Start.AddMinutes(10)), "minimizing does not end the process session");
        Assert.True(tracker.Update(1, Start.AddMinutes(11)), "returning to the game must not restart replay");
        Assert.Equal(1, opens, "open the identity handle once, not on every poll");
        Assert.Equal(0, game.DisposeCalls);
    }

    [Test]
    public static void Closing_the_last_game_stops_after_the_existing_grace()
    {
        var game = new Session(1);
        using var tracker = new GameActivityTracker(_ => game, null);
        tracker.Update(1, Start);
        tracker.Update(null, Start.AddSeconds(100));
        game.IsAlive = false;
        Assert.True(tracker.Update(null, Start.AddSeconds(102)), "retain exit grace");
        Assert.True(!tracker.Update(null, Start.AddSeconds(116)), "the closed game must not keep recording indefinitely");
        Assert.Equal(1, game.DisposeCalls, "release the exit handle");
    }

    [Test]
    public static void Closing_a_second_game_keeps_the_first_alive()
    {
        var first = new Session(1);
        var second = new Session(2);
        using var tracker = new GameActivityTracker(pid => pid == 1 ? first : second, null);
        tracker.Update(1, Start);
        tracker.Update(2, Start.AddSeconds(1));
        second.IsAlive = false;
        Assert.True(tracker.Update(null, Start.AddMinutes(1)), "the first game still owns a replay session");
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(0, first.DisposeCalls);
    }

    [Test]
    public static void Reused_pid_does_not_inherit_a_dead_game_handle()
    {
        var game = new Session(1);
        var browser = new Session(1, @"C:\Browser\msedge.exe");
        IGameProcessSession current = game;
        using var tracker = new GameActivityTracker(_ => current, null);
        tracker.Update(1, Start);
        game.IsAlive = false;
        current = browser;
        tracker.Update(1, Start.AddSeconds(2));
        Assert.True(!tracker.Update(null, Start.AddSeconds(18)), "the new browser PID must not inherit the game session");
        Assert.Equal(1, game.DisposeCalls);
        Assert.Equal(1, browser.DisposeCalls);
    }

    [Test]
    public static void Fullscreen_browser_uses_foreground_grace_without_process_latching()
    {
        var browser = new Session(3, @"C:\Browser\brave.exe");
        using var tracker = new GameActivityTracker(_ => browser, null);
        Assert.True(tracker.Update(3, Start), "existing fullscreen detection is retained");
        Assert.True(!tracker.Update(null, Start.AddSeconds(16)), "leaving F11 must not retain the browser for hours");
        Assert.Equal(1, browser.DisposeCalls);
    }

    [Test]
    public static void Own_settings_window_does_not_start_a_game_session()
    {
        const string ownPath = @"C:\KairosRX\Kairos.exe";
        var settings = new Session(5, ownPath);
        using var tracker = new GameActivityTracker(_ => settings, ownPath);
        Assert.True(!tracker.Update(5, Start), "exclude the settings helper, not only the engine PID");
        Assert.Equal(1, settings.DisposeCalls);
    }

    [Test]
    public static void Unverifiable_process_has_only_bounded_grace()
    {
        using var tracker = new GameActivityTracker(_ => null, null);
        Assert.True(tracker.Update(7, Start), "preserve existing behavior for protected processes");
        Assert.True(!tracker.Update(null, Start.AddSeconds(16)), "never latch an unknown PID indefinitely");
    }

    [Test]
    public static void Disposing_releases_all_handles_once()
    {
        var first = new Session(1);
        var second = new Session(2);
        var tracker = new GameActivityTracker(pid => pid == 1 ? first : second, null);
        Assert.True(!tracker.Update(null, Start), "ordinary windows do not start replay");
        tracker.Update(1, Start);
        tracker.Update(2, Start);
        tracker.Dispose();
        tracker.Dispose();
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Test]
    public static void Native_session_opens_a_read_only_identity_handle()
    {
        using var session = GameProcessSession.TryOpen(Environment.ProcessId);
        Assert.True(session != null && session.IsAlive, "the current process must be queryable without debug access");
        Assert.Equal(Environment.ProcessId, session!.ProcessId);
        Assert.True(!string.IsNullOrWhiteSpace(session.ExecutablePath), "identity must include an executable path");
    }

    [Test]
    public static void Native_game_session_survives_focus_loss_and_ends_when_its_process_exits()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");
        using var child = System.Diagnostics.Process.Start(startInfo)!;
        try
        {
            using var tracker = new GameActivityTracker(GameProcessSession.TryOpen, null);
            Assert.True(tracker.Update(child.Id, Start), "native identity must start a session");
            Assert.True(tracker.Update(null, Start.AddMinutes(10)), "focus loss cannot end a live native session");
            child.Kill();
            Assert.True(child.WaitForExit(5000), "owned test process must exit");
            Assert.True(tracker.Update(null, Start.AddMinutes(10).AddSeconds(1)), "exit keeps the normal grace");
            Assert.True(!tracker.Update(null, Start.AddMinutes(10).AddSeconds(16)), "signalled native handle must end the session");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(5000);
            }
        }
    }
}
