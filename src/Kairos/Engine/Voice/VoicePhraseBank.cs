using System.Text.Json;

namespace Kairos.Engine.Voice;

public sealed record VoicePhraseInfo(string Id, string Name, string Action, bool Enabled);
public sealed record VoicePhraseCommand(string Operation, string? Id = null, string? Action = null,
    string? Name = null, bool? Enabled = null);

/// <summary>
/// A small, cached phrase catalog. A catalog points only to complete, immutable sets of three WAVs;
/// replacing it is the commit point, so interrupted recording and writes keep the previous phrase.
/// The legacy files are imported once and kept for compatibility, never used to resurrect a deletion.
/// Call from the listener's lock; this class does not synchronize its callers.
/// </summary>
public sealed class VoicePhraseBank
{
    public const int MaxPhrasesPerAction = 5;
    public const int MaxNameLength = 60;
    public const string ClipLegacyId = "legacy-clip", ShotLegacyId = "legacy-shot";
    const int MaxCatalogBytes = 64 * 1024, MaxSampleBytes = 512 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly string _dir;
    List<Entry> _entries = [];
    IReadOnlyList<VoicePhraseInfo> _catalog = Array.Empty<VoicePhraseInfo>();
    bool _loaded, _hasManifest, _hasEnabled;
    string? _loadError;

    sealed record Entry(string Id, string Name, string Action, bool Enabled, string Revision);
    sealed record Manifest(int Version, List<Entry>? Phrases);

    public VoicePhraseBank(string dir) => _dir = Path.GetFullPath(dir);
    public string CatalogPath => Path.Combine(_dir, "phrases.json");
    string StoragePath => Path.Combine(_dir, "phrases");
    public IReadOnlyList<VoicePhraseInfo> Catalog { get { EnsureLoaded(); return _catalog; } }
    public bool HasError { get { EnsureLoaded(); return _loadError != null; } }
    public bool HasEnabled { get { EnsureLoaded(); return _hasEnabled; } }

    public static string ActionName(VoiceAction action) => action == VoiceAction.Shot ? "shot" : "clip";
    public static VoiceAction ParseAction(string? action) => action switch
    {
        "clip" => VoiceAction.Clip,
        "shot" => VoiceAction.Shot,
        _ => throw new ArgumentException("voice.err.command"),
    };

    public static string NormalizeName(string? name)
    {
        string value = name?.Trim() ?? "";
        if (value.Length is 0 or > MaxNameLength || value.Any(char.IsControl))
            throw new ArgumentException("voice.err.name");
        return value;
    }

    static bool SafeToken(string? token) => token is { Length: 32 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool ValidId(string? id) => id is ClipLegacyId or ShotLegacyId || id != null && SafeToken(id);
    static bool ValidActionId(string id, string action) => action is "clip" or "shot"
        && (id != ClipLegacyId || action == "clip") && (id != ShotLegacyId || action == "shot");

    public VoicePhraseInfo? Find(string? id) => ValidId(id) ? Catalog.FirstOrDefault(p => p.Id == id) : null;
    public string? Revision(string id) { EnsureLoaded(); return _entries.FirstOrDefault(p => p.Id == id)?.Revision; }

    public VoicePhraseInfo Draft(VoiceAction action, string? name = null, string? id = null)
    {
        RequireWritable();
        string key = ActionName(action);
        int count = _entries.Count(p => p.Action == key);
        if (count >= MaxPhrasesPerAction)
            throw new ArgumentException("voice.err.limit");
        id ??= Guid.NewGuid().ToString("N");
        if (!ValidId(id) || !ValidActionId(id, key) || Find(id) != null)
            throw new ArgumentException("voice.err.command");
        return new VoicePhraseInfo(id, NormalizeName(name ?? $"{(action == VoiceAction.Shot ? "Screenshot" : "Clip")} {count + 1}"), key, true);
    }

    /// <summary>Refresh only at explicit legacy operations/configuration, never on each status poll.</summary>
    public void RefreshLegacy()
    {
        EnsureLoaded();
        if (!_hasManifest && _loadError == null)
        {
            try { ImportLegacy(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { FailRead(e); }
        }
    }

    public List<float[]> LoadSamples(string id)
    {
        EnsureLoaded();
        var entry = _entries.FirstOrDefault(p => p.Id == id);
        if (entry == null)
            return [];
        string folder = RevisionPath(entry.Revision);
        var samples = new List<float[]>(3);
        for (int i = 1; i <= 3; i++)
            samples.Add(ReadSample(Path.Combine(folder, $"sample{i}.wav")));
        return samples;
    }

    static float[] ReadSample(string path)
    {
        long bytes = new FileInfo(path).Length;
        if (bytes is < 44 or > MaxSampleBytes || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("voice.err.storage");
        var audio = WavFile.Read(path);
        if (audio.Length == 0 || audio.Length > WavFile.SampleRate * 3 || audio.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException("voice.err.storage");
        return audio;
    }

    public void Save(VoicePhraseInfo phrase, IReadOnlyList<float[]> samples, bool mirrorLegacy = false)
    {
        RequireWritable();
        if (!ValidId(phrase.Id) || !ValidActionId(phrase.Id, phrase.Action))
            throw new ArgumentException("voice.err.command");
        phrase = phrase with { Name = NormalizeName(phrase.Name) };
        if (samples.Count != 3 || samples.Any(s => s.Length == 0 || s.Length > WavFile.SampleRate * 3 || s.Any(v => !float.IsFinite(v))))
            throw new ArgumentException("voice.err.storage");
        var existing = _entries.FirstOrDefault(p => p.Id == phrase.Id);
        if (existing != null && existing.Action != phrase.Action)
            throw new ArgumentException("voice.err.command");
        if (existing == null && _entries.Count(p => p.Action == phrase.Action) >= MaxPhrasesPerAction)
            throw new ArgumentException("voice.err.limit");

        string revision = WriteSamples(samples);
        var next = _entries.Where(p => p.Id != phrase.Id).ToList();
        var replacement = new Entry(phrase.Id, phrase.Name, phrase.Action, phrase.Enabled, revision);
        int index = existing == null ? next.Count : _entries.IndexOf(existing);
        next.Insert(index, replacement);
        try { Commit(next); }
        catch { TryDeleteRevision(revision); throw; }
        if (existing != null)
            TryDeleteRevision(existing.Revision);
        if (mirrorLegacy && phrase.Id is ClipLegacyId or ShotLegacyId)
        {
            // Compatibility files are not the catalog's source of truth. Its immutable set is already committed.
            try { VoicePhrase.SaveSamples(_dir, samples, ParseAction(phrase.Action)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ObsLog.Warn($"voice legacy mirror: {e.Message}"); }
        }
    }

    public void Rename(string id, string? name) => Update(id, p => p with { Name = NormalizeName(name) });
    public void SetEnabled(string id, bool enabled) => Update(id, p => p with { Enabled = enabled });

    public void Delete(string id)
    {
        RequireWritable();
        var entry = Require(id);
        Commit(_entries.Where(p => p.Id != id).ToList());
        TryDeleteRevision(entry.Revision);
        if (id is ClipLegacyId or ShotLegacyId)
        {
            try { VoicePhrase.DeleteSamples(_dir, ParseAction(entry.Action)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ObsLog.Warn($"voice legacy delete: {e.Message}"); }
        }
    }

    void Update(string id, Func<Entry, Entry> change)
    {
        RequireWritable();
        var entry = Require(id);
        var next = _entries.ToList();
        next[next.IndexOf(entry)] = change(entry);
        Commit(next);
    }

    Entry Require(string id) => ValidId(id) && _entries.FirstOrDefault(p => p.Id == id) is { } p
        ? p : throw new ArgumentException("voice.err.missing");

    void RequireWritable()
    {
        EnsureLoaded();
        if (_loadError != null)
            throw new InvalidDataException("voice.err.storage");
    }

    void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        _hasManifest = File.Exists(CatalogPath);
        if (!_hasManifest)
        {
            try { ImportLegacy(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { FailRead(e); }
            return;
        }
        try
        {
            if (new FileInfo(CatalogPath).Length > MaxCatalogBytes)
                throw new InvalidDataException("catalog too large");
            var saved = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(CatalogPath), Json);
            if (saved is not { Version: 1, Phrases: { } entries } || entries.Count > 2 * MaxPhrasesPerAction)
                throw new InvalidDataException("invalid catalog");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var revisions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || !ValidId(entry.Id) || !SafeToken(entry.Revision) || !ids.Add(entry.Id) || !revisions.Add(entry.Revision)
                    || entry.Action is not ("clip" or "shot") || NormalizeName(entry.Name) != entry.Name
                    || entry.Id == ClipLegacyId && entry.Action != "clip" || entry.Id == ShotLegacyId && entry.Action != "shot")
                    throw new InvalidDataException("invalid catalog entry");
                // Refuse the entire catalog instead of silently removing a damaged phrase on the next edit.
                string folder = RevisionPath(entry.Revision);
                for (int i = 1; i <= 3; i++)
                    ReadSample(Path.Combine(folder, $"sample{i}.wav"));
            }
            if (entries.GroupBy(p => p.Action).Any(g => g.Count() > MaxPhrasesPerAction))
                throw new InvalidDataException("too many phrases");
            _entries = entries;
            RebuildCatalog();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { FailRead(e); }
    }

    void FailRead(Exception error)
    {
        _loadError = error.Message;
        _entries = [];
        RebuildCatalog();
        ObsLog.Warn($"voice catalog kept unchanged: {error.Message}");
    }

    void ImportLegacy()
    {
        var imported = new List<Entry>();
        try
        {
            foreach (var action in new[] { VoiceAction.Clip, VoiceAction.Shot })
            {
                if (!VoicePhrase.HasSamples(_dir, action))
                    continue;
                string[] paths = Enumerable.Range(1, 3).Select(i => Path.Combine(_dir, $"{(action == VoiceAction.Shot ? "shot" : "phrase")}{i}.wav")).ToArray();
                var samples = paths.Select(ReadSample).ToList();
                imported.Add(new Entry(action == VoiceAction.Shot ? ShotLegacyId : ClipLegacyId,
                    action == VoiceAction.Shot ? "Screenshot 1" : "Clip 1", ActionName(action), true, WriteSamples(samples, paths)));
            }
            if (imported.Count > 0)
                Commit(imported);
        }
        catch
        {
            foreach (var entry in imported)
                TryDeleteRevision(entry.Revision);
            throw;
        }
    }

    string WriteSamples(IReadOnlyList<float[]> samples, IReadOnlyList<string>? copyFrom = null)
    {
        // Validate existing storage before creating any staging files inside it.
        RevisionPath(new string('0', 32));
        Directory.CreateDirectory(StoragePath);
        string revision = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(StoragePath, ".pending-" + revision);
        Directory.CreateDirectory(staging);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                string target = Path.Combine(staging, $"sample{i + 1}.wav");
                if (copyFrom == null)
                    WavFile.Write(target, samples[i]);
                else
                    File.Copy(copyFrom[i], target);
            }
            Directory.Move(staging, RevisionPath(revision));
            return revision;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    string RevisionPath(string revision)
    {
        if (!SafeToken(revision))
            throw new InvalidDataException("voice.err.storage");
        string path = Path.Combine(StoragePath, revision);
        // A catalog never supplies a relative path; reject linked storage as well.
        if (Directory.Exists(StoragePath) && (File.GetAttributes(StoragePath) & FileAttributes.ReparsePoint) != 0
            || Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("voice.err.storage");
        return path;
    }

    void Commit(List<Entry> entries)
    {
        Directory.CreateDirectory(_dir);
        string temp = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Manifest(1, entries), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, CatalogPath, overwrite: true);
            _entries = entries;
            _hasManifest = true;
            RebuildCatalog();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    void RebuildCatalog()
    {
        _catalog = Array.AsReadOnly(_entries.Select(p => new VoicePhraseInfo(p.Id, p.Name, p.Action, p.Enabled)).ToArray());
        _hasEnabled = _entries.Exists(p => p.Enabled);
    }

    void TryDeleteRevision(string revision)
    {
        try { string path = RevisionPath(revision); if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { ObsLog.Warn($"voice sample cleanup: {e.Message}"); }
    }
}
