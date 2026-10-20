using System.Text.Json;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Engine.Voice;
using Kairos.Ui;

namespace Kairos.Tests;

public static class VoicePhraseUiTests
{
    static readonly VoicePhraseInfo Clip = new("clip-a", "Save that", "clip", true);
    static readonly VoicePhraseInfo Shot = new("shot-a", "Snap that", "shot", false);
    static VoiceStatus Voice(params VoicePhraseInfo[] phrases) => new(true, phrases.Any(p => p.Action == "clip"), "idle", 0,
        null, null, false, 0.1, HasShotPhrase: phrases.Any(p => p.Action == "shot"), Phrases: phrases);
    static StatusDto Status(VoiceStatus voice) => new(null, new Dictionary<string, bool>(), null, null, false, false, "test", voice);
    static string Reply(VoiceStatus voice) => JsonSerializer.Serialize(Status(voice), Ipc.Json);

    [Test]
    public static void Polling_reuses_phrase_rows_and_keeps_unsaved_names() => OnSta(() =>
    {
        var vm = new SettingsViewModel();
        try
        {
            vm.SeedStatus(Status(Voice(Clip, Shot)));
            var row = vm.VoiceClipPhrases[0];
            row.DraftName = "A name still being typed";
            int listChanges = 0, rowChanges = 0;
            vm.VoiceClipPhrases.CollectionChanged += (_, _) => listChanges++;
            row.PropertyChanged += (_, _) => rowChanges++;
            for (int i = 0; i < 20; i++)
                vm.SeedStatus(Status(Voice(Clip with { }, Shot with { })));
            Assert.True(ReferenceEquals(row, vm.VoiceClipPhrases[0]), "polling keeps the same row and input control");
            Assert.Equal("A name still being typed", row.DraftName);
            Assert.Equal(0, listChanges, "identical lists do not rebuild the items control");
            Assert.Equal(0, rowChanges, "identical row values do not rebind the inputs");
            vm.SeedStatus(Status(Voice(Clip with { Name = "External rename" }, Shot)));
            Assert.Equal("External rename", row.Name, "the saved name updates from the engine");
            Assert.Equal("A name still being typed", row.DraftName, "a simultaneous external edit preserves the local draft");
        }
        finally { vm.Stop(); }
    });

    [Test]
    public static void Row_commands_target_their_phrase_and_names_save_explicitly() => OnSta(() =>
    {
        var sent = new List<VoicePhraseCommand>();
        var current = Voice(Clip, Shot);
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                var edit = JsonSerializer.Deserialize<VoicePhraseCommand>(command["voice-phrase ".Length..], Ipc.Json)!;
                sent.Add(edit);
                if (edit.Operation == "rename")
                    current = Voice(Clip with { Name = edit.Name! }, Shot);
                if (edit.Operation == "enabled")
                    current = Voice(Clip with { Name = "My clip", Enabled = edit.Enabled!.Value }, Shot);
                return Task.FromResult<string?>(Reply(current));
            },
        };
        try
        {
            vm.SeedStatus(Status(current));
            var row = vm.VoiceClipPhrases[0];
            row.DraftName = " My clip ";
            Assert.Equal(0, sent.Count, "typing does not send a command per keystroke");
            row.RenameCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceCanEdit && row.Name == "My clip"), "the explicit save is acknowledged");
            Assert.Equal(new VoicePhraseCommand("rename", Clip.Id, Name: "My clip"), sent[0]);
            row.Enabled = false;
            Assert.True(PumpUntil(() => vm.VoiceCanEdit), "the toggle reply completes");
            Assert.Equal(new VoicePhraseCommand("enabled", Clip.Id, Enabled: false), sent[1]);
            row.TestCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceCanEdit), "a disabled phrase can still be tested");
            Assert.Equal(new VoicePhraseCommand("test", Clip.Id), sent[2]);
            row.RerecordCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceCanEdit), "rerecord reply completes");
            Assert.Equal(new VoicePhraseCommand("rerecord", Clip.Id), sent[3]);
            row.DeleteCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceCanEdit), "delete reply completes");
            Assert.Equal(new VoicePhraseCommand("delete", Clip.Id), sent[4]);
        }
        finally { vm.Stop(); }
    });

    [Test]
    public static void New_phrase_is_not_listed_until_the_three_recordings_are_saved() => OnSta(() =>
    {
        VoicePhraseCommand? sent = null;
        var enrolling = Voice(Clip) with { Mode = "enrolling", ActivePhraseId = "clip-b", ActivePhraseName = "Great play" };
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                sent = JsonSerializer.Deserialize<VoicePhraseCommand>(command["voice-phrase ".Length..], Ipc.Json);
                return Task.FromResult<string?>(Reply(enrolling));
            },
        };
        try
        {
            vm.SeedStatus(Status(Voice(Clip)));
            vm.NewVoiceClipName = " Great play ";
            vm.VoiceAddClipCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceEnrolling), "recording starts through IPC");
            Assert.Equal(new VoicePhraseCommand("enroll", Action: "clip", Name: "Great play"), sent);
            Assert.Equal(1, vm.VoiceClipPhrases.Count);
            Assert.True(!vm.VoiceClipPhrases[0].CanEdit && !vm.VoiceAddShotCommand.CanExecute(null), "conflicting edits are disabled");
            Assert.True(vm.VoiceCancelCommand.CanExecute(null), "cancel remains available");
            Assert.True(vm.VoiceSessionTitle.Contains("Great play", StringComparison.Ordinal), "the active phrase is identified");
            vm.SeedStatus(Status(enrolling with { Samples = 2 }));
            Assert.Equal(1, vm.VoiceClipPhrases.Count, "partial enrollment is not shown as saved");
            Assert.True(vm.VoiceDone1 && vm.VoiceDone2 && !vm.VoiceDone3, "the three recording indicators reflect progress");
            vm.SeedStatus(Status(Voice(Clip, new("clip-b", "Great play", "clip", true)) with { Message = "voice.enrolled" }));
            Assert.Equal(2, vm.VoiceClipPhrases.Count);
            Assert.True(vm.VoiceCanEdit && vm.VoiceSavedNote.Length > 0, "completed enrollment re-enables editing and confirms saving");
        }
        finally
        {
            vm.SeedStatus(Status(Voice(Clip)));
            vm.Stop();
        }
    });

    [Test]
    public static void List_limits_are_separate_for_each_action() => OnSta(() =>
    {
        var vm = new SettingsViewModel();
        try
        {
            var clips = Enumerable.Range(0, 5).Select(i => new VoicePhraseInfo($"c{i}", $"Clip {i}", "clip", true)).ToArray();
            vm.SeedStatus(Status(Voice(clips)));
            vm.NewVoiceClipName = "Full";
            vm.NewVoiceShotName = "Room";
            Assert.True(!vm.VoiceClipCanAdd && !vm.VoiceAddClipCommand.CanExecute(null), "a full action cannot start another enrollment");
            Assert.True(vm.VoiceShotCanAdd && vm.VoiceAddShotCommand.CanExecute(null), "the other action retains its own space");
            Assert.True(vm.VoiceClipListTitle.Contains("5/5", StringComparison.Ordinal), "the action limit is visible");
            Assert.True(vm.VoiceShotEmpty && !vm.VoiceClipEmpty, "empty states are independent");
        }
        finally { vm.Stop(); }
    });

    [Test]
    public static void Phrase_errors_are_localized_and_raw_server_errors_are_not_shown() => OnSta(() =>
    {
        LanguageTests.WithLanguage("es", () =>
        {
            string? reply = "{\"error\":\"voice.err.storage\"}";
            var vm = new SettingsViewModel { RequestAsync = (_, _) => Task.FromResult<string?>(reply) };
            try
            {
                vm.SeedStatus(Status(Voice(Clip)));
                _ = vm.SendVoicePhraseAsync(new("rename", Clip.Id, Name: "New"));
                Assert.True(PumpUntil(() => vm.VoiceCanEdit && vm.VoiceNote.Length > 0), "the server failure reaches the UI");
                Assert.Equal(Text.Get("voice.err.storage"), vm.VoiceNote);
                reply = "{\"error\":\"C:\\\\secret\\\\file.json failed\"}";
                _ = vm.SendVoicePhraseAsync(new("delete", Clip.Id));
                Assert.True(PumpUntil(() => vm.VoiceCanEdit && vm.VoiceNote == Text.Get("voice.err.command")), "unknown server details become a useful localized error");
                reply = "broken-json";
                _ = vm.SendVoicePhraseAsync(new("delete", Clip.Id));
                Assert.True(PumpUntil(() => vm.VoiceCanEdit), "invalid replies do not leave the controls locked");
                Assert.Equal(Text.Get("voice.err.command"), vm.VoiceNote);
                reply = null;
                _ = vm.SendVoicePhraseAsync(new("delete", Clip.Id));
                Assert.True(PumpUntil(() => vm.VoiceCanEdit), "a disconnected engine unlocks the controls");
                Assert.Equal(Text.Get("voice.err.engine"), vm.VoiceNote);
            }
            finally { vm.Stop(); }
        });
    });

    [Test]
    public static void Tests_name_the_match_and_explain_ambiguous_results() => OnSta(() =>
    {
        var vm = new SettingsViewModel();
        try
        {
            vm.SeedStatus(Status(Voice(Clip, Shot) with { Mode = "testing", LastScore = 0.02, LastMatch = true,
                LastPhraseId = Shot.Id, LastPhraseName = Shot.Name, LastPhrase = "shot" }));
            Assert.True(vm.VoiceResult.Contains(Shot.Name, StringComparison.Ordinal), "test results identify the actual phrase");
            vm.SeedStatus(Status(Voice(Clip, Shot) with { Mode = "testing", LastScore = 0.08, LastMatch = false, Message = "voice.err.ambiguous" }));
            Assert.Equal(Text.Get("voice.err.ambiguous"), vm.VoiceResult);
            Assert.True(!vm.VoiceLastMatch, "ambiguity is not displayed as success");
        }
        finally { vm.SeedStatus(Status(Voice(Clip))); vm.Stop(); }
    });

    [Test]
    public static void An_unreadable_catalog_is_visible_without_a_fake_active_session() => OnSta(() =>
    {
        var vm = new SettingsViewModel();
        try
        {
            vm.SeedStatus(Status(Voice() with { Message = "voice.err.storage" }));
            Assert.Equal(Text.Get("voice.err.storage"), vm.VoiceNote, "an idle storage error is shown above the lists");
            Assert.True(!vm.VoiceSessionVisible && !vm.VoiceBusy, "an error does not imply recording or testing");
            Assert.True(vm.VoiceClipEmpty && vm.VoiceShotEmpty, "an unreadable list has no invented entries");
        }
        finally { vm.Stop(); }
    });

    [Test]
    public static void Closing_during_a_pending_enrollment_cancels_after_its_reply() => OnSta(() =>
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new ManualResetEventSlim();
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                if (command == "voice-cancel") { cancelled.Set(); return Task.FromResult<string?>(null); }
                return pending.Task;
            },
        };
        vm.SeedStatus(Status(Voice(Clip)));
        var request = vm.SendVoicePhraseAsync(new("test", Clip.Id));
        vm.Stop();
        Assert.True(!cancelled.IsSet, "the request has not entered testing yet");
        pending.SetResult(Reply(Voice(Clip) with { Mode = "testing" }));
        Assert.True(cancelled.Wait(1500), "a late-starting test is cancelled after the window closes");
        Assert.True(request.Wait(1500) && vm.FinishVoiceRequestsAsync().Wait(1500), "process-exit cleanup completes without a dispatcher loop");
        Assert.True(!vm.VoiceTesting, "a late response cannot update a closed window");
    });

    [Test]
    public static void Closing_after_the_transport_reply_cancels_before_the_ui_callback_can_run() => OnSta(() =>
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellations = 0;
        var idle = Voice(Clip);
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                if (command == "voice-cancel")
                {
                    Interlocked.Increment(ref cancellations);
                    return Task.FromResult<string?>(Reply(idle));
                }
                return pending.Task;
            },
        };
        vm.SeedStatus(Status(idle));
        var request = vm.SendVoicePhraseAsync(new("enroll", Action: "clip", Name: "Later"));
        pending.SetResult(Reply(idle with { Mode = "enrolling", ActivePhraseName = "Later" }));
        Assert.True(request.Wait(1500), "the transport completes while the dispatcher is deliberately held");
        Assert.True(!vm.VoiceEnrolling, "the queued UI callback has not changed the old idle snapshot");
        vm.Stop();
        vm.Stop();
        Assert.True(vm.FinishVoiceRequestsAsync().Wait(1500), "closing cleanup does not depend on the queued UI callback");
        Assert.Equal(1, cancellations, "a replied but undisplayed microphone request is cancelled exactly once");
        Assert.True(!vm.VoiceEnrolling, "the stale UI callback cannot restore enrollment after close");
    });

    [Test]
    public static void Cancel_after_the_transport_reply_wins_over_the_queued_starting_ui_callback() => OnSta(() =>
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellations = 0, displayedActive = 0;
        var idle = Voice(Clip);
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                if (command == "voice-cancel")
                {
                    Interlocked.Increment(ref cancellations);
                    return Task.FromResult<string?>(Reply(idle));
                }
                return pending.Task;
            },
        };
        try
        {
            vm.SeedStatus(Status(idle));
            vm.PropertyChanged += (_, _) => { if (vm.VoiceEnrolling) displayedActive++; };
            var request = vm.SendVoicePhraseAsync(new("enroll", Action: "clip", Name: "Later"));
            pending.SetResult(Reply(idle with { Mode = "enrolling", ActivePhraseName = "Later" }));
            Assert.True(request.Wait(1500), "the dispatcher is held until the starting transport finishes");
            vm.VoiceCancelCommand.Execute(null);
            vm.VoiceCancelCommand.Execute(null);
            Assert.True(PumpUntil(() => vm.VoiceCanEdit), "the cancellation reply restores idle controls");
            Assert.Equal(1, cancellations, "the cancellation is not dropped or duplicated in the transport/UI gap");
            Assert.Equal(0, displayedActive, "the queued starting reply is suppressed once cancel is requested");
            Assert.True(!vm.VoiceSessionVisible, "the cancelled enrollment is never restored by the old callback");
        }
        finally { vm.SeedStatus(Status(idle)); vm.Stop(); pending.TrySetResult(null); }
    });

    [Test]
    public static void Repeated_cancel_clicks_wait_for_the_pending_reply_and_cancel_once() => OnSta(() =>
    {
        var startReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellations = 0;
        var idle = Voice(Clip);
        var active = idle with { Mode = "enrolling", ActivePhraseName = "Later", ActivePhraseId = "clip-b" };
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                if (command == "voice-cancel") { Interlocked.Increment(ref cancellations); return cancelReply.Task; }
                return startReply.Task;
            },
        };
        try
        {
            vm.SeedStatus(Status(idle));
            _ = vm.SendVoicePhraseAsync(new("enroll", Action: "clip", Name: "Later"));
            vm.SeedStatus(Status(active)); // The status poll can observe the operation before its reply arrives.
            vm.VoiceCancelCommand.Execute(null);
            vm.VoiceCancelCommand.Execute(null);
            Assert.Equal(0, cancellations, "cancel waits behind the operation that starts the microphone session");
            startReply.SetResult(Reply(active));
            Assert.True(PumpUntil(() => Volatile.Read(ref cancellations) == 1), "one queued cancellation is sent after the starting reply");
            Assert.True(!vm.VoiceCanEdit, "controls stay locked until cancellation finishes");
            vm.VoiceCancelCommand.Execute(null);
            vm.VoiceCancelCommand.Execute(null);
            cancelReply.SetResult(Reply(idle));
            Assert.True(PumpUntil(() => vm.VoiceCanEdit), "the cancellation status unlocks the controls");
            Assert.Equal(1, cancellations, "repeated clicks cannot send more cancellations");
            Assert.True(!vm.VoiceSessionVisible, "the stale starting reply never restores the session");
        }
        finally
        {
            startReply.TrySetResult(Reply(idle));
            cancelReply.TrySetResult(Reply(idle));
            vm.SeedStatus(Status(idle));
            vm.Stop();
        }
    });

    [Test]
    public static void A_late_enrollment_reply_does_not_cancel_a_reopened_window() => OnSta(() =>
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelled = 0;
        var current = Voice(Clip);
        var vm = new SettingsViewModel
        {
            RequestAsync = (command, _) =>
            {
                if (command.StartsWith("voice-phrase ", StringComparison.Ordinal)) return pending.Task;
                if (command == "voice-cancel") Interlocked.Increment(ref cancelled);
                return Task.FromResult<string?>(command == "caps" ? null : Reply(current));
            },
        };
        try
        {
            vm.SeedStatus(Status(current));
            var request = vm.SendVoicePhraseAsync(new("enroll", Action: "clip", Name: "Later"));
            vm.Stop();
            vm.Start();
            pending.SetResult(Reply(current with { Mode = "enrolling" }));
            Assert.True(request.Wait(1500), "the stale reply completes");
            Assert.True(vm.FinishVoiceRequestsAsync().Wait(1500), "old cleanup also completes after reopening");
            PumpUntil(() => false, 40);
            Assert.Equal(0, cancelled, "a request from the old lifetime cannot cancel the new session");
            Assert.True(!vm.VoiceEnrolling && vm.VoiceCanEdit, "the new window retains its current status");
        }
        finally { vm.Stop(); pending.TrySetResult(null); }
    });

    static bool PumpUntil(Func<bool> condition, int timeoutMs = 1500)
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
        var thread = new Thread(() => { try { test(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception(error.ToString());
    }
}
