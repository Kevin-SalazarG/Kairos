using Kairos.Settings;

namespace Kairos.Engine.Voice;

/// <summary>What the settings window shows about Smart Save. Added catalog fields are optional for old clients.</summary>
public sealed record VoiceStatus(
    bool Available, bool HasPhrase, string Mode, int Samples, string? Message,
    double? LastScore, bool LastMatch, double Threshold, bool ModelsFound = true,
    bool HasShotPhrase = false, string EnrollingFor = "clip", string? LastPhrase = null,
    IReadOnlyList<VoicePhraseInfo>? Phrases = null, string? ActivePhraseId = null, string? ActivePhraseName = null,
    string? LastPhraseId = null, string? LastPhraseName = null, int MaxPhrasesPerAction = VoicePhraseBank.MaxPhrasesPerAction);

/// <summary>
/// Microphone callbacks only enqueue audio. One below-normal worker performs VAD, embeds each utterance
/// once and compares it with the enabled phrases. A separate control lock keeps file/model work away
/// from the audio lock, and generations prevent a cancelled recording from publishing late results.
/// </summary>
public sealed class VoiceTrigger : IDisposable
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);
    const int MaxQueuedBlocks = 400;
    readonly string _modelsDir;
    readonly object _gate = new(), _control = new();
    readonly VoicePhraseBank _bank;
    readonly Dictionary<string, (string Revision, VoicePhrase Phrase)> _built = new(StringComparer.Ordinal);
    readonly Queue<float[]> _queue = new();
    readonly AutoResetEvent _wake = new(false);
    readonly List<float[]> _samples = [];
    IReadOnlyList<VoicePhraseInfo> _catalog = Array.Empty<VoicePhraseInfo>();
    Thread? _worker;
    volatile bool _stopping;
    SpeechModels? _models;
    VoicePhraseInfo? _activePhrase;
    string? _testPhraseId;
    bool _legacyEnrollment, _enabled, _micConnected, _modelsFound, _storageError, _hasEnabled;
    VoiceSensitivity _sensitivity = VoiceSensitivity.Normal;
    VoiceAction _enrollFor = VoiceAction.Clip;
    string _mode = "idle";
    string? _message, _lastPhrase, _lastPhraseId, _lastPhraseName;
    double? _lastScore, _lastThreshold;
    bool _lastMatch;
    readonly Dictionary<VoiceAction, DateTime> _lastTrigger = new() { [VoiceAction.Clip] = DateTime.MinValue, [VoiceAction.Shot] = DateTime.MinValue };
    DateTime _testStarted;
    int _generation, _firstStatus = 1;
    long _blocks, _chunks;

    public long BlocksReceived => Interlocked.Read(ref _blocks);
    public long ChunksAnalysed => Interlocked.Read(ref _chunks);
    public static bool ShouldLog(string mode, double score, double threshold) => mode == "testing" || score < 1.5 * threshold;
    public event Action<VoiceAction>? Triggered;

    public VoiceTrigger(string modelsDir, string dataDir)
    {
        _modelsDir = modelsDir;
        _bank = new VoicePhraseBank(dataDir);
        _modelsFound = SpeechModels.Available(_modelsDir);
        RefreshCatalog();
    }

    public bool Listening
    {
        get { lock (_gate) return !_stopping && (_mode != "idle" || _enabled && _hasEnabled); }
    }

    public void Configure(bool enabled, VoiceSensitivity sensitivity)
    {
        lock (_control)
        {
            RefreshCatalog(refreshLegacy: true);
            bool models = SpeechModels.Available(_modelsDir);
            lock (_gate)
            {
                if (_enabled != enabled || _sensitivity != sensitivity)
                {
                    _generation++;
                    _queue.Clear();
                }
                _enabled = enabled;
                _sensitivity = sensitivity;
                _modelsFound = models;
            }
            EnsureWorker();
        }
    }

    public void SetMicConnected(bool connected) { lock (_gate) _micConnected = connected; }

    /// <summary>48 kHz mono, safe to call on libobs's audio thread.</summary>
    public void Feed(ReadOnlySpan<float> samples48k)
    {
        if (!Listening)
            return;
        var copy = samples48k.ToArray();
        lock (_gate)
        {
            if (_stopping || !(_mode != "idle" || _enabled && _hasEnabled))
                return;
            Interlocked.Increment(ref _blocks);
            _queue.Enqueue(copy);
            if (_queue.Count > MaxQueuedBlocks)
                _queue.Dequeue();
            _wake.Set();
        }
    }

    /// <summary>Compatibility: rerecord the action's first phrase, or create its legacy phrase.</summary>
    public void StartEnroll(VoiceAction action = VoiceAction.Clip)
    {
        lock (_control)
        {
            RefreshCatalog(refreshLegacy: true);
            string key = VoicePhraseBank.ActionName(action);
            var phrase = _bank.Catalog.FirstOrDefault(p => p.Action == key)
                ?? _bank.Draft(action, id: action == VoiceAction.Shot ? VoicePhraseBank.ShotLegacyId : VoicePhraseBank.ClipLegacyId);
            BeginEnroll(phrase, legacy: true);
        }
    }

    public void StartTest() { lock (_control) BeginTest(null); }
    public void Cancel() { lock (_control) SetMode("idle", clearSamples: true); }

    /// <summary>Compatibility: delete only the action's first phrase.</summary>
    public void DeletePhrase(VoiceAction action = VoiceAction.Clip)
    {
        lock (_control)
        {
            RefreshCatalog(refreshLegacy: true);
            var phrase = _bank.Catalog.FirstOrDefault(p => p.Action == VoicePhraseBank.ActionName(action));
            if (phrase != null)
                Delete(phrase.Id);
            else
                SetMode("idle", clearSamples: true);
        }
    }

    /// <summary>Named, validated catalog operations used by the settings window and IPC.</summary>
    public void Execute(VoicePhraseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_control)
        {
            if (_stopping)
                throw new InvalidOperationException("voice.err.command");
            RefreshCatalog();
            switch (command.Operation)
            {
                case "enroll":
                case "rerecord":
                    VoicePhraseInfo phrase;
                    if (command.Id != null)
                    {
                        phrase = Require(command.Id);
                        if (command.Action != null && command.Action != phrase.Action)
                            throw new ArgumentException("voice.err.command");
                        if (command.Name != null)
                            phrase = phrase with { Name = VoicePhraseBank.NormalizeName(command.Name) };
                    }
                    else
                    {
                        if (command.Operation == "rerecord")
                            throw new ArgumentException("voice.err.missing");
                        phrase = _bank.Draft(VoicePhraseBank.ParseAction(command.Action), command.Name);
                    }
                    BeginEnroll(phrase, legacy: false);
                    break;
                case "test": BeginTest(command.Id); break;
                case "rename":
                    RequireIdle();
                    _bank.Rename(Require(command.Id).Id, command.Name);
                    RefreshCatalog();
                    InvalidateAudio();
                    break;
                case "enabled":
                    RequireIdle();
                    if (command.Enabled is not { } enabled)
                        throw new ArgumentException("voice.err.command");
                    _bank.SetEnabled(Require(command.Id).Id, enabled);
                    RefreshCatalog();
                    InvalidateAudio();
                    EnsureWorker();
                    break;
                case "delete":
                    RequireIdle();
                    Delete(Require(command.Id).Id);
                    break;
                default: throw new ArgumentException("voice.err.command");
            }
        }
    }

    VoicePhraseInfo Require(string? id) => _bank.Find(id) ?? throw new ArgumentException("voice.err.missing");
    void RequireIdle() { lock (_gate) if (_mode != "idle") throw new InvalidOperationException("voice.err.busy"); }

    void BeginEnroll(VoicePhraseInfo phrase, bool legacy)
    {
        _modelsFound = SpeechModels.Available(_modelsDir);
        lock (_gate)
        {
            _activePhrase = phrase;
            _testPhraseId = null;
            _enrollFor = VoicePhraseBank.ParseAction(phrase.Action);
            _legacyEnrollment = legacy;
        }
        SetMode("enrolling", clearSamples: true, preserveActive: true);
    }

    void BeginTest(string? id)
    {
        RefreshCatalog(refreshLegacy: true);
        var phrase = id == null ? null : Require(id);
        if (_bank.Catalog.Count == 0)
            return;
        _modelsFound = SpeechModels.Available(_modelsDir);
        lock (_gate)
        {
            _activePhrase = phrase;
            _testPhraseId = id;
            _testStarted = DateTime.UtcNow;
        }
        SetMode("testing", clearSamples: true, preserveActive: true);
    }

    void Delete(string id)
    {
        _bank.Delete(id);
        _built.Remove(id);
        RefreshCatalog();
        SetMode("idle", clearSamples: true);
    }

    void RefreshCatalog(bool refreshLegacy = false)
    {
        // Call with _control held (the constructor runs before publication).
        if (refreshLegacy)
            _bank.RefreshLegacy();
        var catalog = _bank.Catalog;
        bool error = _bank.HasError;
        bool hasEnabled = _bank.HasEnabled;
        lock (_gate) { _catalog = catalog; _storageError = error; _hasEnabled = hasEnabled; }
    }

    public VoiceStatus Status()
    {
        // Compatibility with callers that write legacy samples just after constructing the listener.
        // This happens once, never once per UI poll.
        if (Interlocked.Exchange(ref _firstStatus, 0) == 1)
            lock (_control) RefreshCatalog(refreshLegacy: true);
        lock (_gate)
        {
            if (_mode == "testing" && DateTime.UtcNow - _testStarted > TestTimeout)
                ResetMode("idle", clearSamples: true, preserveActive: false);
            return new VoiceStatus(_micConnected && _modelsFound, _catalog.Any(p => p.Action == "clip"), _mode,
                _samples.Count, _storageError ? "voice.err.storage" : _message, _lastScore, _lastMatch,
                _lastThreshold ?? VoiceSettings.Threshold(_sensitivity), _modelsFound, _catalog.Any(p => p.Action == "shot"),
                VoicePhraseBank.ActionName(_enrollFor), _lastPhrase, _catalog, _activePhrase?.Id, _activePhrase?.Name,
                _lastPhraseId, _lastPhraseName);
        }
    }

    void InvalidateAudio() { lock (_gate) { _generation++; _queue.Clear(); } }

    void SetMode(string mode, bool clearSamples, bool preserveActive = false)
    {
        lock (_gate) ResetMode(mode, clearSamples, preserveActive);
        EnsureWorker();
    }

    void ResetMode(string mode, bool clearSamples, bool preserveActive)
    {
        _mode = mode;
        _message = null;
        _lastScore = _lastThreshold = null;
        _lastPhrase = _lastPhraseId = _lastPhraseName = null;
        _lastMatch = false;
        _generation++;
        _queue.Clear();
        if (clearSamples)
            _samples.Clear();
        if (!preserveActive)
        {
            _activePhrase = null;
            _testPhraseId = null;
        }
    }

    void EnsureWorker()
    {
        // Every caller holds _control; there can only be one model owner/worker.
        if (_worker != null || _stopping || !Listening)
            return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Smart Save", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    void Run()
    {
        try { RunWorker(); }
        finally { _models?.Dispose(); _models = null; }
    }

    void RunWorker()
    {
        var resampler = new Resampler48To16();
        var segmenter = new VoiceSegmenter();
        VadState? vad = null;
        var pending = new List<float>();
        var chunk = new float[VoiceSegmenter.Chunk];
        int generation = -1;
        while (!_stopping)
        {
            _wake.WaitOne(500);
            while (!_stopping)
            {
                float[] block;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                        break;
                    block = _queue.Dequeue();
                    if (_generation != generation)
                    {
                        generation = _generation;
                        resampler = new Resampler48To16();
                        segmenter = _mode == "enrolling"
                            ? new VoiceSegmenter(VoiceSegmenter.EnrollHangoverChunks, withContext: false)
                            : new VoiceSegmenter();
                        vad = null;
                        pending.Clear();
                    }
                }
                try
                {
                    var models = Models();
                    if (models == null)
                        continue;
                    vad ??= models.NewVad();
                    pending.AddRange(resampler.Process(block));
                    int used = 0;
                    for (; used + VoiceSegmenter.Chunk <= pending.Count; used += VoiceSegmenter.Chunk)
                    {
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending).Slice(used, VoiceSegmenter.Chunk).CopyTo(chunk);
                        Interlocked.Increment(ref _chunks);
                        foreach (var segment in segmenter.Push(chunk, models.SpeechProbability(vad, chunk)))
                            Handle(models, segment, generation);
                    }
                    pending.RemoveRange(0, used);
                }
                catch (Exception e)
                {
                    ObsLog.Warn($"smart save: {e.Message}");
                    lock (_gate)
                        if (generation == _generation && _mode == "enrolling")
                            _message = "voice.err.storage";
                }
            }
        }
    }

    SpeechModels? Models()
    {
        if (_models == null && _modelsFound)
            _models = new SpeechModels(_modelsDir);
        return _models;
    }

    void Handle(SpeechModels models, float[] segment, int generation)
    {
        string mode;
        IReadOnlyList<VoicePhraseInfo> catalog;
        string? testId;
        lock (_gate)
        {
            if (generation != _generation || _stopping)
                return;
            mode = _mode;
            catalog = _catalog;
            testId = _testPhraseId;
        }
        if (mode == "enrolling")
        {
            Enroll(models, segment, generation, catalog);
            return;
        }

        string? testAction = testId == null ? null : catalog.FirstOrDefault(p => p.Id == testId)?.Action;
        // A row test also checks other actions, including disabled phrases the user might enable later.
        var candidates = catalog.Where(p => mode == "testing"
            ? testId == null || p.Id == testId || p.Action != testAction : p.Enabled).ToArray();
        if (candidates.Length == 0)
            return;
        var query = PhraseMatcher.PrepareQuery(models.Embed(segment));
        var scored = new List<(VoicePhraseInfo Info, double Score, double Threshold)>(candidates.Length);
        VoiceSensitivity sensitivity;
        lock (_gate) sensitivity = _sensitivity;
        foreach (var info in candidates)
        {
            lock (_gate) if (generation != _generation || _stopping) return;
            if (PhraseFor(models, info.Id) is { } phrase)
                scored.Add((info, phrase.Score(query), VoiceSettings.Threshold(sensitivity, phrase.Spread)));
            lock (_gate) if (generation != _generation || _stopping) return;
        }
        if (scored.Count == 0)
            return;
        var closest = scored.MinBy(p => p.Score / p.Threshold);
        var clips = scored.Where(p => p.Info.Action == "clip").ToArray();
        var shots = scored.Where(p => p.Info.Action == "shot").ToArray();
        var clip = clips.Length == 0 ? default : clips.MinBy(p => p.Score / p.Threshold);
        var shot = shots.Length == 0 ? default : shots.MinBy(p => p.Score / p.Threshold);
        VoiceAction? trigger = null;
        lock (_gate)
        {
            if (generation != _generation || _stopping)
                return;
            double? clipScore = clip.Info == null ? null : clip.Score;
            double? shotScore = shot.Info == null ? null : shot.Score;
            double clipThreshold = clip.Info == null ? VoiceSettings.Threshold(sensitivity) : clip.Threshold;
            double shotThreshold = shot.Info == null ? VoiceSettings.Threshold(sensitivity) : shot.Threshold;
            var heard = VoiceRouting.Pick(clipScore, clipThreshold, shotScore, shotThreshold);
            _lastScore = closest.Score;
            _lastThreshold = closest.Threshold;
            _lastPhrase = closest.Info.Action;
            _lastPhraseId = closest.Info.Id;
            _lastPhraseName = closest.Info.Name;
            _lastMatch = heard != null && (testId == null || VoicePhraseBank.ActionName(heard.Value) == testAction);
            if (mode == "testing")
                _message = VoiceRouting.Ambiguous(clipScore, clipThreshold, shotScore, shotThreshold) ? "voice.err.ambiguous" : null;
            if (ShouldLog(mode, closest.Score, closest.Threshold))
                ObsLog.Info($"voice {mode}: {closest.Info.Action} phrase score {closest.Score:F3} (threshold {closest.Threshold:F3}) over {segment.Length / (double)SpeechModels.SampleRate:F1} s");
            DateTime now = DateTime.UtcNow;
            if (mode == "idle" && _enabled && heard is { } action && VoiceRouting.Ready(_lastTrigger[action], now))
            {
                _lastTrigger[action] = now;
                trigger = action;
            }
        }
        if (trigger is { } act)
            Triggered?.Invoke(act);
    }

    void Enroll(SpeechModels models, float[] segment, int generation, IReadOnlyList<VoicePhraseInfo> catalog)
    {
        List<float[]> earlier;
        VoicePhraseInfo target;
        bool legacy;
        lock (_gate)
        {
            if (generation != _generation || _activePhrase == null)
                return;
            earlier = [.. _samples];
            target = _activePhrase;
            legacy = _legacyEnrollment;
        }
        var problem = VoicePhrase.Check(models, segment, earlier);
        bool tooClose = false;
        if (problem == EnrollProblem.None)
        {
            var otherActions = catalog.Where(p => p.Action != target.Action).ToArray();
            if (otherActions.Length != 0)
            {
                var query = PhraseMatcher.PrepareQuery(models.Embed(segment));
                tooClose = otherActions.Any(p => PhraseFor(models, p.Id) is { } other
                    && VoiceRouting.TooClose(other.Score(query), VoiceSettings.Threshold(VoiceSensitivity.Easy, other.Spread)));
            }
        }
        if (problem != EnrollProblem.None || tooClose)
        {
            lock (_gate)
                if (generation == _generation)
                    _message = tooClose ? "voice.err.tooClose" : problem switch
                    {
                        EnrollProblem.TooShort => "voice.err.short",
                        EnrollProblem.TooLong => "voice.err.long",
                        _ => "voice.err.different",
                    };
            return;
        }
        var trimmed = VoicePhrase.Trim(segment);
        lock (_gate)
        {
            if (generation != _generation)
                return;
            if (_samples.Count < 2)
            {
                _samples.Add(trimmed);
                _message = null;
                return;
            }
            earlier = [.. _samples, trimmed];
        }
        // Build all nine templates before committing any recording; cancellation remains responsive.
        var built = VoicePhrase.Build(models, earlier);
        lock (_control)
        {
            lock (_gate)
                if (generation != _generation || _stopping)
                    return;
            _bank.Save(target, earlier, mirrorLegacy: legacy);
            _built[target.Id] = (_bank.Revision(target.Id)!, built);
            RefreshCatalog();
            LogPhrase(built);
            lock (_gate)
            {
                ResetMode("idle", clearSamples: true, preserveActive: false);
                _message = "voice.enrolled";
            }
        }
    }

    VoicePhrase? PhraseFor(SpeechModels models, string id)
    {
        string? revision;
        List<float[]> samples;
        lock (_control)
        {
            revision = _bank.Revision(id);
            if (revision == null)
                return null;
            if (_built.TryGetValue(id, out var cached) && cached.Revision == revision)
                return cached.Phrase;
            samples = _bank.LoadSamples(id);
        }
        var built = samples.Count == 3 ? VoicePhrase.Build(models, samples) : null;
        if (built == null)
            return null;
        lock (_control)
        {
            if (_bank.Revision(id) != revision)
                return null;
            _built[id] = (revision, built);
        }
        LogPhrase(built);
        return built;
    }

    static void LogPhrase(VoicePhrase phrase) =>
        ObsLog.Info($"voice phrase: spread {phrase.Spread:F3}; thresholds strict {VoiceSettings.Threshold(VoiceSensitivity.Strict, phrase.Spread):F3}, normal {VoiceSettings.Threshold(VoiceSensitivity.Normal, phrase.Spread):F3}, easy {VoiceSettings.Threshold(VoiceSensitivity.Easy, phrase.Spread):F3}");

    public void Dispose()
    {
        lock (_control)
        {
            if (_stopping)
                return;
            lock (_gate) { _stopping = true; _generation++; _queue.Clear(); _wake.Set(); }
        }
        // The worker owns and disposes its ONNX sessions, even if an in-flight inference needs longer.
        if (_worker == null || _worker.Join(2000))
            _wake.Dispose();
    }
}
