using System.Text.Json;
using Kairos.App;
using Kairos.Engine.Voice;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Named phrase integration through the same microphone queue, VAD and models as the app.</summary>
public static class VoicePhraseListTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
    static float[] Load(string name) => WavFile.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Voice", name + ".wav"));
    static float[][] English() => [Load("pos_en_v1_r1"), Load("pos_en_v1_r2"), Load("pos_en_v1_r3")];
    static float[][] Arabic() => [Load("pos_ar_v2_r1"), Load("pos_ar_v2_r2"), Load("pos_ar_v2_r3")];
    static VoiceTrigger New(string dir)
    {
        var trigger = new VoiceTrigger(SpeechModels.DefaultDirectory, dir);
        trigger.SetMicConnected(true);
        return trigger;
    }

    static void Say(VoiceTrigger v, float[] audio)
    {
        var at48 = new float[(audio.Length + 12800) * 3];
        for (int i = 0; i < audio.Length; i++)
            at48[3 * i] = at48[3 * i + 1] = at48[3 * i + 2] = audio[i];
        for (int i = 0; i + 480 <= at48.Length; i += 480)
            v.Feed(at48.AsSpan(i, 480));
    }

    static bool WaitFor(Func<bool> condition, int ms = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    [Test]
    public static void New_named_training_is_committed_after_three_samples_and_cancelling_a_rerecord_keeps_it()
    {
        string dir = Temp();
        try
        {
            using var v = New(dir);
            v.Execute(new("enroll", Action: "clip", Name: "Guarda la jugada"));
            var draft = v.Status();
            string id = draft.ActivePhraseId!;
            Assert.True(VoicePhraseBank.ValidId(id), "draft has a safe unique ID");
            Assert.Equal("Guarda la jugada", draft.ActivePhraseName);
            Assert.Equal(0, draft.Phrases!.Count);
            for (int r = 1; r <= 3; r++)
            {
                Say(v, Load($"pos_en_v1_r{r}"));
                int expected = r;
                Assert.True(WaitFor(() => expected < 3 ? v.Status().Samples == expected : v.Status().Mode == "idle"), $"sample {r}: {v.Status().Message}");
                if (r < 3)
                    Assert.Equal(0, v.Status().Phrases!.Count, "partial phrase stays out of saved list");
            }
            Assert.Equal(1, v.Status().Phrases!.Count);
            Assert.Equal(id, v.Status().Phrases![0].Id);
            string manifest = Path.Combine(dir, "phrases.json");
            byte[] saved = File.ReadAllBytes(manifest);
            v.Execute(new("rerecord", Id: id));
            Say(v, Load("pos_en_v1_r4"));
            Assert.True(WaitFor(() => v.Status().Samples == 1), "rerecord accepted a sample");
            v.Cancel();
            Assert.True(saved.SequenceEqual(File.ReadAllBytes(manifest)), "cancel leaves original recording and label unchanged");
            v.Execute(new("enroll", Action: "shot", Name: "Nueva captura"));
            v.Cancel();
            Assert.True(saved.SequenceEqual(File.ReadAllBytes(manifest)), "cancelled new draft is never saved");
            var restarted = new VoicePhraseBank(dir);
            Assert.Equal(id, restarted.Catalog.Single().Id);
            Assert.Equal("Guarda la jugada", restarted.Catalog.Single().Name);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Multiple_phrases_for_one_action_can_be_tested_individually_even_when_all_are_disabled()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var en = bank.Draft(VoiceAction.Clip, "English") with { Enabled = false };
            var ar = bank.Draft(VoiceAction.Clip, "Arabic") with { Enabled = false };
            bank.Save(en, English());
            bank.Save(ar, Arabic());
            using var v = New(dir);
            v.Configure(true, VoiceSensitivity.Normal);
            Assert.Equal(false, v.Listening, "disabled entries do not keep microphone listening");
            int triggers = 0;
            v.Triggered += _ => Interlocked.Increment(ref triggers);
            v.Execute(new("test", Id: en.Id));
            Assert.Equal(true, v.Listening, "explicit disabled phrase test listens");
            Say(v, Load("pos_en_v1_r0"));
            Assert.True(WaitFor(() => v.Status().LastMatch), "English phrase matched");
            Assert.Equal(en.Id, v.Status().LastPhraseId);
            Assert.Equal("English", v.Status().LastPhraseName);
            v.Execute(new("test", Id: ar.Id));
            Say(v, Load("pos_ar_v2_r0"));
            Assert.True(WaitFor(() => v.Status().LastMatch), "Arabic phrase matched");
            Assert.Equal(ar.Id, v.Status().LastPhraseId);
            Assert.Equal(0, triggers, "testing never saves a clip");
            v.Cancel();
            Assert.Equal(false, v.Listening);
            v.Execute(new("enabled", Id: en.Id, Enabled: true));
            Assert.Equal(true, v.Listening);
            v.Execute(new("enabled", Id: en.Id, Enabled: false));
            Assert.Equal(false, v.Listening, "last enabled phrase switched off");
            using var restarted = New(dir);
            Assert.True(restarted.Status().Phrases!.All(p => !p.Enabled), "enabled states persisted");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Disabled_phrases_do_not_trigger_while_a_different_enabled_action_remains_available()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var en = bank.Draft(VoiceAction.Clip, "Clip") with { Enabled = false };
            bank.Save(en, English());
            var ar = bank.Draft(VoiceAction.Shot, "Picture");
            bank.Save(ar, Arabic());
            using var v = New(dir);
            v.Configure(true, VoiceSensitivity.Normal);
            int clips = 0, shots = 0;
            v.Triggered += a => { if (a == VoiceAction.Clip) Interlocked.Increment(ref clips); else Interlocked.Increment(ref shots); };
            Say(v, Load("pos_en_v1_r0"));
            Assert.True(WaitFor(() => v.Status().LastScore != null), "utterance evaluated");
            Assert.Equal(false, v.Status().LastMatch);
            Assert.Equal(0, clips);
            Assert.Equal(0, shots);
            Say(v, Load("pos_ar_v2_r0"));
            Assert.True(WaitFor(() => Volatile.Read(ref shots) == 1), "enabled picture phrase acts");
            Assert.Equal(0, clips);
            Assert.Equal(ar.Id, v.Status().LastPhraseId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Conflicting_legacy_phrases_are_rejected_during_targeted_test_and_normal_listening()
    {
        string dir = Temp();
        try
        {
            VoicePhrase.SaveSamples(dir, English());
            VoicePhrase.SaveSamples(dir, English(), VoiceAction.Shot);
            using var v = New(dir);
            v.Configure(true, VoiceSensitivity.Normal);
            int triggers = 0;
            v.Triggered += _ => Interlocked.Increment(ref triggers);
            v.Execute(new("test", Id: VoicePhraseBank.ClipLegacyId));
            Say(v, Load("pos_en_v1_r0"));
            Assert.True(WaitFor(() => v.Status().Message == "voice.err.ambiguous"), "selected test reports action ambiguity");
            Assert.Equal(false, v.Status().LastMatch, "does not report a safe match");
            Assert.Equal(0, triggers);
            v.Cancel();
            Say(v, Load("pos_en_v1_r0"));
            Assert.True(WaitFor(() => v.Status().LastScore != null), "normal listener evaluated utterance");
            Assert.Equal(false, v.Status().LastMatch);
            Assert.Equal(0, triggers, "ambiguous speech never chooses an action");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Status_and_ipc_keep_named_metadata_without_polling_the_catalog_from_disk()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var phrase = bank.Draft(VoiceAction.Clip, "Mi frase");
            bank.Save(phrase, English());
            using var v = New(dir);
            var status = v.Status();
            using var locked = new FileStream(bank.CatalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            for (int i = 0; i < 100; i++)
                Assert.True(ReferenceEquals(status.Phrases, v.Status().Phrases), "unchanged catalog is reused");
            string json = JsonSerializer.Serialize(new VoicePhraseCommand("rename", phrase.Id, Name: "Nueva"), Ipc.Json);
            var decoded = JsonSerializer.Deserialize<VoicePhraseCommand>(json, Ipc.Json)!;
            Assert.Equal("rename", decoded.Operation);
            Assert.Equal(phrase.Id, decoded.Id);
            Assert.Equal("Nueva", decoded.Name);
            var roundTrip = JsonSerializer.Deserialize<VoiceStatus>(JsonSerializer.Serialize(status, Ipc.Json), Ipc.Json)!;
            Assert.Equal("Mi frase", roundTrip.Phrases!.Single().Name);
            Assert.Equal(5, roundTrip.MaxPhrasesPerAction);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
