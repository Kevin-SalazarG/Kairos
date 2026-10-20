using System.Text.Json;
using Kairos.Engine.Voice;

namespace Kairos.Tests;

public static class VoicePhraseBankTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
    static float[][] Samples(float value = 0.1f) => [[value, -value, value], [value, value], [-value, value]];
    static void ThrowsKey(Action action, string key)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
        { Assert.Equal(key, e.Message); return; }
        throw new Exception("expected " + key);
    }

    [Test]
    public static void Legacy_migration_keeps_exact_audio_and_does_not_resurrect_deleted_phrases()
    {
        string dir = Temp();
        try
        {
            VoicePhrase.SaveSamples(dir, Samples());
            VoicePhrase.SaveSamples(dir, Samples(0.2f), VoiceAction.Shot);
            byte[] before = File.ReadAllBytes(Path.Combine(dir, "phrase1.wav"));
            var bank = new VoicePhraseBank(dir);
            Assert.Equal(2, bank.Catalog.Count);
            string revision = bank.Revision(VoicePhraseBank.ClipLegacyId)!;
            Assert.True(before.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "phrases", revision, "sample1.wav"))), "import is byte exact");
            Assert.True(before.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "phrase1.wav"))), "original preserved during migration");
            Assert.Equal(3, bank.LoadSamples(VoicePhraseBank.ClipLegacyId).Count);
            bank.Rename(VoicePhraseBank.ClipLegacyId, "  Guarda la jugada  ");
            bank.SetEnabled(VoicePhraseBank.ClipLegacyId, false);
            var restarted = new VoicePhraseBank(dir);
            var saved = restarted.Find(VoicePhraseBank.ClipLegacyId)!;
            Assert.Equal("Guarda la jugada", saved.Name);
            Assert.Equal(false, saved.Enabled);
            bank.Delete(VoicePhraseBank.ClipLegacyId);
            // Even old external files reappearing cannot bring the deleted entry back.
            VoicePhrase.SaveSamples(dir, Samples());
            restarted = new VoicePhraseBank(dir);
            restarted.RefreshLegacy();
            Assert.Equal(1, restarted.Catalog.Count);
            Assert.Equal(null, restarted.Find(VoicePhraseBank.ClipLegacyId));
            Assert.True(restarted.Find(VoicePhraseBank.ShotLegacyId) != null, "other action retained");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Five_phrases_per_action_include_disabled_entries_and_other_actions_have_their_own_limit()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            for (int i = 0; i < VoicePhraseBank.MaxPhrasesPerAction; i++)
            {
                var phrase = bank.Draft(VoiceAction.Clip, $"Clip {i}") with { Enabled = false };
                bank.Save(phrase, Samples());
            }
            Assert.Equal(false, bank.HasEnabled);
            ThrowsKey(() => bank.Draft(VoiceAction.Clip), "voice.err.limit");
            bank.Save(bank.Draft(VoiceAction.Shot, "Captura"), Samples());
            Assert.Equal(6, bank.Catalog.Count);
            Assert.Equal(true, bank.HasEnabled);
            Assert.Equal(6, new VoicePhraseBank(dir).Catalog.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Invalid_names_ids_and_reserved_action_mismatches_never_change_the_catalog()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var phrase = bank.Draft(VoiceAction.Clip, "Valid");
            bank.Save(phrase, Samples());
            byte[] before = File.ReadAllBytes(bank.CatalogPath);
            foreach (var name in new[] { "", "  ", "a\nb", new string('x', 61) })
                ThrowsKey(() => bank.Rename(phrase.Id, name), "voice.err.name");
            foreach (var id in new[] { "../outside", "..\\outside", "", "C:\\outside", new string('a', 33) })
            {
                Assert.Equal(null, bank.Find(id));
                Assert.Equal(0, bank.LoadSamples(id).Count);
                ThrowsKey(() => bank.Delete(id), "voice.err.missing");
            }
            ThrowsKey(() => bank.Draft(VoiceAction.Shot, id: VoicePhraseBank.ClipLegacyId), "voice.err.command");
            ThrowsKey(() => bank.Save(new VoicePhraseInfo(VoicePhraseBank.ShotLegacyId, "Wrong", "clip", true), Samples()), "voice.err.command");
            Assert.True(before.SequenceEqual(File.ReadAllBytes(bank.CatalogPath)), "invalid commands leave disk unchanged");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void A_failed_replacement_keeps_the_previous_catalog_and_samples()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var phrase = bank.Draft(VoiceAction.Clip, "Original");
            bank.Save(phrase, Samples());
            string revision = bank.Revision(phrase.Id)!;
            byte[] before = File.ReadAllBytes(bank.CatalogPath);
            using (var locked = new FileStream(bank.CatalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool failed = false;
                try { bank.Save(phrase with { Name = "Replacement" }, Samples(0.6f)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
                Assert.True(failed, "locked catalog rejects replacement");
                Assert.Equal(revision, bank.Revision(phrase.Id));
                Assert.Equal("Original", bank.Find(phrase.Id)!.Name);
            }
            Assert.True(before.SequenceEqual(File.ReadAllBytes(bank.CatalogPath)), "previous manifest intact");
            var restarted = new VoicePhraseBank(dir);
            Assert.Equal(revision, restarted.Revision(phrase.Id));
            Assert.Near(0.1, restarted.LoadSamples(phrase.Id)[0][0], 0.0001);
            Assert.Equal(1, Directory.GetDirectories(Path.Combine(dir, "phrases")).Length, "failed sample set cleaned");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Corrupt_catalogs_are_reported_and_never_overwritten_or_replaced_with_legacy_data()
    {
        string[] corrupt =
        [
            "{",
            "{\"version\":2,\"phrases\":[]}",
            "{\"version\":1,\"phrases\":[{\"id\":\"legacy-clip\",\"name\":\"Clip\",\"action\":\"clip\",\"enabled\":true,\"revision\":null}]}",
            "{\"version\":1,\"phrases\":[{\"id\":\"legacy-clip\",\"name\":\"Clip\",\"action\":\"clip\",\"enabled\":true,\"revision\":\"../outside\"}]}",
            "{\"version\":1,\"phrases\":[{\"id\":\"legacy-clip\",\"name\":\"Clip\",\"action\":\"clip\",\"enabled\":true,\"revision\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}",
        ];
        foreach (string json in corrupt)
        {
            string dir = Temp();
            try
            {
                Directory.CreateDirectory(dir);
                VoicePhrase.SaveSamples(dir, Samples());
                string path = Path.Combine(dir, "phrases.json");
                File.WriteAllText(path, json);
                var bank = new VoicePhraseBank(dir);
                Assert.Equal(0, bank.Catalog.Count);
                Assert.Equal(true, bank.HasError);
                ThrowsKey(() => bank.Draft(VoiceAction.Clip), "voice.err.storage");
                bank.RefreshLegacy();
                Assert.Equal(json, File.ReadAllText(path));
                Assert.True(VoicePhrase.HasSamples(dir), "legacy recordings remain recoverable");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }

    [Test]
    public static void Incomplete_replacements_and_mutable_callers_cannot_change_saved_audio_or_status()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var phrase = bank.Draft(VoiceAction.Shot, "Picture");
            bank.Save(phrase, Samples());
            string revision = bank.Revision(phrase.Id)!;
            var catalog = bank.Catalog;
            ThrowsKey(() => bank.Save(phrase, [Samples()[0], Samples()[1]]), "voice.err.storage");
            ThrowsKey(() => bank.Save(phrase, [[float.NaN], [1], [1]]), "voice.err.storage");
            Assert.Equal(revision, bank.Revision(phrase.Id));
            Assert.True(ReferenceEquals(catalog, bank.Catalog), "stable catalog is cached");
            Assert.True(bank.Catalog is not VoicePhraseInfo[], "catalog cannot be cast to mutable backing array");
            using var locked = new FileStream(bank.CatalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            for (int i = 0; i < 100; i++)
                Assert.True(ReferenceEquals(catalog, bank.Catalog), "polling does not reopen catalog");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Corrupt_saved_audio_is_reported_before_listening_and_the_catalog_is_preserved()
    {
        string dir = Temp();
        try
        {
            var bank = new VoicePhraseBank(dir);
            var phrase = bank.Draft(VoiceAction.Clip, "Saved");
            bank.Save(phrase, Samples());
            byte[] manifest = File.ReadAllBytes(bank.CatalogPath);
            string sample = Path.Combine(dir, "phrases", bank.Revision(phrase.Id)!, "sample1.wav");
            // A tiny WAV whose data header claims a huge allocation must fail before being marked ready.
            byte[] corrupt = File.ReadAllBytes(sample);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(40, 4), int.MaxValue);
            File.WriteAllBytes(sample, corrupt);
            var restarted = new VoicePhraseBank(dir);
            Assert.Equal(0, restarted.Catalog.Count);
            Assert.Equal(true, restarted.HasError);
            Assert.Equal(false, restarted.HasEnabled);
            using var trigger = new VoiceTrigger(Path.Combine(dir, "no-models"), dir);
            trigger.Configure(true, Kairos.Settings.VoiceSensitivity.Normal);
            Assert.Equal("voice.err.storage", trigger.Status().Message);
            Assert.Equal(false, trigger.Listening);
            ThrowsKey(() => restarted.Draft(VoiceAction.Clip), "voice.err.storage");
            Assert.True(manifest.SequenceEqual(File.ReadAllBytes(bank.CatalogPath)), "catalog retained for repair");
            Assert.True(corrupt.SequenceEqual(File.ReadAllBytes(sample)), "damaged audio retained for repair");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public static void Close_action_scores_are_rejected_instead_of_guessing_and_clear_matches_still_route()
    {
        Assert.Equal(null, VoiceRouting.Pick(0.030, 0.060, 0.032, 0.060));
        Assert.Equal(null, VoiceRouting.Pick(0.030, 0.060, 0.030, 0.060));
        Assert.Equal(VoiceAction.Clip, VoiceRouting.Pick(0.020, 0.060, 0.040, 0.060));
        Assert.Equal(VoiceAction.Shot, VoiceRouting.Pick(0.040, 0.060, 0.020, 0.060));
        Assert.Equal(VoiceAction.Clip, VoiceRouting.Pick(0.055, 0.060, 0.060, 0.060));
        Assert.Equal(false, VoiceRouting.Ambiguous(null, 0.06, 0.03, 0.06));
    }
}
