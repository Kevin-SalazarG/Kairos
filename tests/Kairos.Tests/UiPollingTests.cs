using System.ComponentModel;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Voice;
using Kairos.Ui;

namespace Kairos.Tests;

public static class UiPollingTests
{
    static StatusDto Status(int qp = 16) => new(
        new EngineStatus(true, false, 60, 0, 0, 600, 10, 120, "h265_texture_amf", 1920, 1080, 300,
            1024, @"E:\Videos\Kairos RX", false, null, RateControl: "CQP", QualityLevel: qp),
        new Dictionary<string, bool> { ["save"] = true }, null, null, false, false, "1.0.1");

    [Test]
    public static void Hotkey_focus_and_blur_requests_wait_for_each_prior_reply() => OnSta(() =>
    {
        var focusReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blurReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var focused = new ManualResetEventSlim();
        using var blurred = new ManualResetEventSlim();
        var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, timeout) =>
            {
                commands.Enqueue(command);
                Assert.Equal(800, timeout, "typing requests retain their timeout");
                if (command == "hotkeys-typing")
                {
                    focused.Set();
                    return focusReply.Task;
                }
                blurred.Set();
                return blurReply.Task;
            },
        };
        try
        {
            vm.SetTypingHotkey(true);
            vm.SetTypingHotkey(false);
            Assert.True(focused.Wait(1500), "focus request was dispatched");
            Assert.True(!blurred.Wait(80), "blur must wait for the focus reply");
            Assert.Equal(1, commands.Count);
            focusReply.SetResult(null);
            Assert.True(blurred.Wait(1500), "blur is dispatched after focus completes");
            Assert.Equal("hotkeys-typing,hotkeys-typed", string.Join(",", commands), "rapid focus changes arrive in order");
        }
        finally
        {
            focusReply.TrySetResult(null);
            blurReply.TrySetResult(null);
            vm.Stop();
        }
    });

    [Test]
    public static void Identical_visible_status_does_not_repeat_notifications() => OnSta(() =>
    {
        var vm = new SettingsViewModel();
        var status = Status();
        vm.SeedStatus(status);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SeedStatus(status with { Engine = status.Engine! with { TotalFrames = 630, Fps = 60.00001 } });

        Assert.Equal(0, changed.Count, "frame counters and imperceptible FPS changes do not rebind the page");
        vm.SeedStatus(status with { Engine = status.Engine! with { WorkingSetMb = 301 } });
        Assert.Equal(nameof(vm.MemoryText), string.Join(",", changed), "only the changed visible value is notified");
        vm.Stop();
    });

    [Test]
    public static void Native_quality_changes_and_file_deletion_still_refresh_the_ui() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), "kairos-poll-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "thumbnail fixture");
        var vm = new SettingsViewModel();
        try
        {
            var status = Status() with { LastClip = path };
            vm.SeedStatus(status);
            Assert.True(vm.HasLastClip, "existing clip is available");
            var changed = new HashSet<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            File.Delete(path);

            vm.SeedStatus(status with { Engine = status.Engine! with { QualityLevel = 14 } });

            Assert.True(changed.Contains(nameof(vm.HasLastClip)) && !vm.HasLastClip, "deletion is checked even with an unchanged path");
            Assert.True(changed.Contains(nameof(vm.LiveCaptureDetailText)) && vm.LiveCaptureDetailText.Contains("CQP 14"),
                "actual native quality remains live");
            Assert.True(!changed.Contains(nameof(vm.LastClipPath)), "unchanged paths do not refresh the Library");
        }
        finally
        {
            vm.Stop();
            File.Delete(path);
        }
    });

    [Test]
    public static void Switching_voice_test_cards_then_cancelling_refreshes_both_cards() => OnSta(() =>
    {
        var vm = new SettingsViewModel();
        var voice = new VoiceStatus(true, true, "testing", 0, null, null, false, 0.1, HasShotPhrase: true);
        var status = Status() with { Voice = voice };
        vm.SeedStatus(status);
        Assert.Equal("testing", vm.VoiceView);
        Assert.Equal("ready", vm.ShotView);
        vm.TestCard = "shot";
        Assert.Equal("testing", vm.ShotView);
        bool shotChanged = false;
        vm.PropertyChanged += (_, e) => shotChanged |= e.PropertyName == nameof(vm.ShotView);

        vm.SeedStatus(status with { Voice = voice with { Mode = "idle" } });

        Assert.True(shotChanged && vm.ShotView == "ready", "cancelling refreshes the card selected outside polling");
        Assert.Equal("ready", vm.VoiceView);
        vm.Stop();
    });

    [Test]
    public static void Reservations_are_compared_by_content_even_when_the_dictionary_is_reused() => OnSta(() =>
    {
        var vm = new SettingsViewModel
        {
            NvidiaKeys = () => [],
            ObsKeys = () => new HashSet<System.Windows.Forms.Keys>(),
            IsKeyFree = _ => true,
        };
        vm.HkSave = "Ctrl+Alt+F11";
        vm.SeedReplay(true);
        var status = Status();
        vm.SeedStatus(status);
        Assert.True(!vm.SaveKey.Taken, "initial reservation is free");

        status.Hotkeys["save"] = false;
        vm.SeedStatus(status);
        Assert.True(vm.SaveKey.Taken, "an in-place reservation change is observed");
        status.Hotkeys["save"] = true;
        vm.SeedStatus(status);
        Assert.True(!vm.SaveKey.Taken, "a released key is observed");
        vm.Stop();
    });

    [Test]
    public static void Repeated_start_and_caps_refresh_keep_one_request_and_stop_discards_answers() => OnSta(() =>
    {
        var statusReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capsReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int statusRequests = 0, capsRequests = 0;
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) => command == "caps" ? Caps() : StatusRequest(),
        };
        Task<string?> Caps() { capsRequests++; return capsReply.Task; }
        Task<string?> StatusRequest() { statusRequests++; return statusReply.Task; }
        vm.SeedStatus(Status());
        vm.Start();
        vm.Start();
        vm.RefreshCapsCommand.Execute(null);
        vm.RefreshCapsCommand.Execute(null);
        int changed = 0;
        vm.PropertyChanged += (_, _) => changed++;
        vm.Stop();
        statusReply.SetResult(JsonSerializer.Serialize(Status(14), Ipc.Json));
        capsReply.SetResult(JsonSerializer.Serialize(new CapsDto([], [], [], [], ["h265_texture_amf"], "32.2.2"), Ipc.Json));
        PumpUntil(() => false, 100);

        Assert.Equal(1, statusRequests, "Start is idempotent and a pending poll does not overlap");
        Assert.Equal(1, capsRequests, "capabilities are single-flight");
        Assert.Equal(0, changed, "late replies cannot update a stopped window");
        Assert.True(vm.LiveCaptureDetailText.Contains("CQP 16"), "late status was ignored");
        Assert.True(!vm.Rx6600ProfileAvailable, "late capabilities were ignored");
    });

    [Test]
    public static void Restart_accepts_only_the_new_poll_generation() => OnSta(() =>
    {
        var replies = new List<TaskCompletionSource<string?>>();
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                if (command == "caps")
                    return Task.FromResult<string?>(null);
                var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                replies.Add(reply);
                return reply.Task;
            },
        };
        vm.SeedStatus(Status());
        vm.Start();
        vm.Stop();
        vm.Start();
        try
        {
            Assert.Equal(2, replies.Count, "restart creates a new poll");
            replies[0].SetResult(JsonSerializer.Serialize(Status(12), Ipc.Json));
            replies[1].SetResult(JsonSerializer.Serialize(Status(14), Ipc.Json));
            Assert.True(PumpUntil(() => vm.LiveCaptureDetailText.Contains("CQP 14"), 1500), "the current poll reaches the dispatcher");
            Assert.True(!vm.LiveCaptureDetailText.Contains("CQP 12"), "a stale poll cannot replace it");
        }
        finally
        {
            vm.Stop();
            foreach (var reply in replies)
                reply.TrySetResult(null);
        }
    });

    [Test]
    public static void Restart_retries_pending_thumbnails_and_discards_older_images() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), "kairos-thumbnail-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "thumbnail fixture");
        var requests = new List<Action<BitmapSource?>>();
        var pendingStatus = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) => command == "caps" ? Task.FromResult<string?>(null) : pendingStatus.Task,
            RequestThumbnail = (_, done) => requests.Add(done),
        };
        try
        {
            vm.SeedStatus(Status() with { LastClip = path });
            vm.Start();
            vm.Stop();
            vm.Start();
            Assert.Equal(3, requests.Count, "pending images are requested again for each new generation");
            var current = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            current.Freeze();
            var stale = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[8], 8);
            stale.Freeze();
            requests[2](current);
            requests[1](stale);
            requests[0](stale);

            Assert.True(PumpUntil(() => ReferenceEquals(current, vm.LastClipThumbnail), 1500), "the current image is published");
            Assert.True(!ReferenceEquals(stale, vm.LastClipThumbnail), "older images cannot replace it");
        }
        finally
        {
            vm.Stop();
            pendingStatus.TrySetResult(null);
            File.Delete(path);
        }
    });

    [Test]
    public static void Status_notification_reuses_its_event_and_compares_values()
    {
        var tracker = StatusNotification<int>.Create("Count", static count => count);
        PropertyChangedEventArgs? first = tracker.ReadChanged(1);
        Assert.True(first?.PropertyName == "Count", "the first value is published");
        Assert.Equal(null, tracker.ReadChanged(1), "equal values are skipped");
        Assert.True(ReferenceEquals(first, tracker.ReadChanged(2)), "later changes reuse the same event object");
        tracker.Invalidate();
        Assert.True(ReferenceEquals(first, tracker.ReadChanged(2)), "an explicit UI refresh invalidates the old comparison baseline");
    }

    static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        return condition();
    }

    static void OnSta(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Exception(error.ToString());
    }
}
