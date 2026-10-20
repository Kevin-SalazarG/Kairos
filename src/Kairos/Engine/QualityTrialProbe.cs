using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Kairos.Engine.Share;
using Kairos.Native;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Isolated same-source AMD encoding probe. It never reads or writes the user's saved settings.</summary>
public static class QualityTrialProbe
{
    // Keep probe-only media controls here; production capture does not need these entry points.
    [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
    static extern void obs_source_media_play_pause(IntPtr source, [MarshalAs(UnmanagedType.U1)] bool pause);

    [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
    static extern void obs_source_media_set_time(IntPtr source, long milliseconds);

    [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
    static extern long obs_source_media_get_time(IntPtr source);

    [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
    static extern uint obs_source_get_width(IntPtr source);

    [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
    static extern uint obs_source_get_height(IntPtr source);

    sealed record Sample(double ElapsedSeconds, long SourceTimeMs, double Fps, uint LaggedFrames,
        uint SkippedFrames, uint TotalFrames, long WorkingSetMb, long PrivateMemoryMb);

    public static int Run(string[] args, TextWriter output)
    {
        CapturePipeline? pipeline = null;
        IntPtr source = IntPtr.Zero;
        string workingDirectory = Directory.GetCurrentDirectory();
        using var recordingStopped = new ManualResetEventSlim();
        using var replaySaved = new ManualResetEventSlim();
        string? recordingPath = null, replayPath = null;
        double recordingClock = 0;
        try
        {
            if (args.Length is < 4 or > 6 || !int.TryParse(args[3], out int qp) || qp is not (14 or 16) ||
                (args.Length >= 5 && (!int.TryParse(args[4], out int parsedSeconds) || parsedSeconds is < 5 or > 140)) ||
                (args.Length == 6 && (!double.TryParse(args[5], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double offset) || !double.IsFinite(offset) || offset < 0)))
            {
                output.WriteLine("usage: --selftest-quality-trial <reference.mp4> <output-folder> <14|16> [seconds:5-140] [start-seconds]");
                return 2;
            }

            string reference = Path.GetFullPath(args[1]);
            if (!File.Exists(reference))
                throw new FileNotFoundException("The reference video was not found.", reference);
            if (string.IsNullOrWhiteSpace(args[2]))
                throw new ArgumentException("An isolated output folder is required.");
            int seconds = args.Length >= 5 ? int.Parse(args[4]) : 20;
            double startSeconds = args.Length == 6 ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0;
            string folder = Path.GetFullPath(args[2]);
            string runId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder);

            ClipTranscoder.Load(AppContext.BaseDirectory);
            MediaInfo input = ClipTranscoder.Probe(reference);
            if (!double.IsFinite(input.Duration) || input.Duration < 1 || input.Width != 1920 || input.Height != 1080 ||
                !double.IsFinite(input.Fps) || Math.Abs(input.Fps - 60) > 0.1)
                throw new ArgumentException("The reference must contain at least one second of 1920x1080 video at 60 fps.");
            string inputHash;
            if (startSeconds >= input.Duration - 1)
                throw new ArgumentException("The start offset must leave at least one second of reference video.");
            using (var file = File.OpenRead(reference))
                inputHash = Convert.ToHexString(SHA256.HashData(file));

            var settings = new AppSettings();
            CaptureProfiles.ApplyRx6600(settings);
            settings.Video.AmdCqp = qp;
            settings.Capture.Mode = CaptureMode.Screen;
            settings.Audio.System = false;
            settings.Audio.Mic = false;
            settings.Audio.SourceAudioOnly = false;
            settings.Audio.SeparateTracks = false;
            settings.Voice.Enabled = false;
            settings.General.OnlyWhenGaming = false;
            settings.Output.Folder = folder;
            settings.Normalize();

            ObsHost.Start(AppContext.BaseDirectory, Path.Combine(folder, "config-" + runId),
                Path.Combine(folder, "logs-" + runId), new VideoConfig(1920, 1080, 1920, 1080, 60), VideoCodec.Hevc);
            ObsHost.LoadLater();
            output.WriteLine($"log: {ObsLog.CurrentPath}");
            if (!CaptureProfiles.IsRx6600Compatible(ObsHost.EncoderIds()))
                throw new InvalidOperationException("The AMD HEVC texture encoder required by this probe is unavailable.");

            pipeline = new CapturePipeline(settings);
            pipeline.Build();
            if (pipeline.Base != (1920, 1080) || pipeline.Output != (1920, 1080))
                throw new InvalidOperationException("This probe requires a 1920x1080 capture canvas without scaling.");
            pipeline.RecordingStopped += (path, duration) =>
            {
                recordingPath = path;
                recordingClock = duration;
                recordingStopped.Set();
            };
            pipeline.ReplaySaved += (path, _) =>
            {
                replayPath = path;
                replaySaved.Set();
            };

            using (var data = new ObsData().Set("is_local_file", true).Set("local_file", reference)
                       .Set("looping", true).Set("restart_on_activate", true).Set("hw_decode", false)
                       .Set("close_when_inactive", false).Set("speed_percent", 100L))
                source = Obs.obs_source_create("ffmpeg_source", "Kairos quality trial", data.Handle, IntPtr.Zero);
            if (source == IntPtr.Zero)
                throw new InvalidOperationException("The local reference media source could not be created.");
            Obs.obs_source_set_audio_mixers(source, 0);
            Obs.obs_source_set_muted(source, true);
            Obs.obs_set_output_source(0, source);

            var ready = Stopwatch.StartNew();
            while ((obs_source_get_width(source) != 1920 || obs_source_get_height(source) != 1080) &&
                   ready.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(25);
            if (obs_source_get_width(source) != 1920 || obs_source_get_height(source) != 1080)
                throw new InvalidOperationException("The local reference did not produce a 1920x1080 picture within 10 seconds.");
            obs_source_media_play_pause(source, true);
            obs_source_media_set_time(source, (long)(startSeconds * 1000));
            Thread.Sleep(250);
            if (!pipeline.SetBufferActive(true))
                throw new InvalidOperationException($"Replay start failed: {pipeline.LastError}");
            Thread.Sleep(500); // Warm the encoder on the paused first picture before measuring motion.

            string target = Path.Combine(folder, $"qp{qp}-recording-{runId}.mp4");
            if (!pipeline.ToggleRecording(target))
                throw new InvalidOperationException($"Recording start failed: {pipeline.LastError}");
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            EngineStatus before = pipeline.GetStatus();
            long sourceStartMs = obs_source_media_get_time(source);
            var samples = new List<Sample>();
            var clock = Stopwatch.StartNew();
            obs_source_media_play_pause(source, false);
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep((int)Math.Min(250, Math.Max(1, (seconds - clock.Elapsed.TotalSeconds) * 1000)));
                process.Refresh();
                EngineStatus current = pipeline.GetStatus();
                samples.Add(new Sample(clock.Elapsed.TotalSeconds, obs_source_media_get_time(source), current.Fps,
                    current.LaggedFrames, current.SkippedFrames, current.TotalFrames,
                    process.WorkingSet64 / (1024 * 1024), process.PrivateMemorySize64 / (1024 * 1024)));
            }
            double elapsed = clock.Elapsed.TotalSeconds;
            process.Refresh();
            double cpuPercent = (process.TotalProcessorTime - cpuBefore).TotalSeconds /
                elapsed / Environment.ProcessorCount * 100;
            EngineStatus after = pipeline.GetStatus();
            long sourceEndMs = obs_source_media_get_time(source);
            obs_source_media_play_pause(source, true);
            pipeline.ToggleRecording();
            if (!recordingStopped.Wait(TimeSpan.FromSeconds(20)) || recordingPath == null || !File.Exists(recordingPath))
                throw new InvalidOperationException("The recording did not finish within 20 seconds.");
            if (!pipeline.SaveReplay(false) || !replaySaved.Wait(TimeSpan.FromSeconds(30)) ||
                replayPath == null || !File.Exists(replayPath))
                throw new InvalidOperationException("The replay did not save within 30 seconds.");

            MediaInfo recording = ClipTranscoder.Probe(recordingPath);
            MediaInfo replay = ClipTranscoder.Probe(replayPath);
            bool Matches(MediaInfo info) => info.VideoCodec == "hevc" && info.Width == 1920 && info.Height == 1080 &&
                Math.Abs(info.Fps - 60) < 0.1;
            bool countersReset = after.TotalFrames < before.TotalFrames || after.LaggedFrames < before.LaggedFrames ||
                after.SkippedFrames < before.SkippedFrames;
            bool sourceAdvanced = samples.Any(s => Math.Abs(s.SourceTimeMs - sourceStartMs) >= 250);
            bool passed = pipeline.Choice?.Vendor == EncoderVendor.Amd && after.RateControl == "CQP" &&
                after.QualityLevel == qp && Matches(recording) && Matches(replay) && sourceAdvanced &&
                Math.Abs(recording.Duration - seconds) <= Math.Max(1, seconds * 0.1) &&
                Math.Abs(recordingClock - seconds) <= 1 && replay.Duration > 2 && !after.FolderFellBack && !countersReset;
            string json = JsonSerializer.Serialize(new
            {
                Passed = passed,
                RequestedQp = qp,
                RequestedSeconds = seconds,
                RequestedSourceStartSeconds = startSeconds,
                ElapsedSeconds = elapsed,
                InputPath = reference,
                InputSha256 = inputHash,
                Input = input,
                SourceStartMs = sourceStartMs,
                SourceEndMs = sourceEndMs,
                SourceAdvanced = sourceAdvanced,
                SourceUsesSoftwareDecoding = true,
                SourceLoops = true,
                CpuPercent = cpuPercent,
                PeakSampledWorkingSetMb = samples.Max(s => s.WorkingSetMb),
                PeakSampledPrivateMemoryMb = samples.Max(s => s.PrivateMemoryMb),
                Before = before,
                After = after,
                CountersReset = countersReset,
                TotalFramesDelta = (long)after.TotalFrames - before.TotalFrames,
                LaggedFramesDelta = (long)after.LaggedFrames - before.LaggedFrames,
                SkippedFramesDelta = (long)after.SkippedFrames - before.SkippedFrames,
                FailedModules = ObsHost.FailedModules.ToArray(),
                RecordingPath = recordingPath,
                RecordingClockSeconds = recordingClock,
                Recording = recording,
                ReplayPath = replayPath,
                Replay = replay,
                ReplayReachedRequestedWindow = seconds >= settings.Replay.Seconds &&
                    replay.Duration >= settings.Replay.Seconds - 2,
                Samples = samples,
                QualityMetricsVerified = false,
                Limitations = "Same-source real-time re-encoding probe; align frames before comparing image quality. " +
                    "A compressed input measures additional compression loss, not fidelity to the original game. " +
                    "CPU includes reference decoding; no game FPS or GPU utilization is measured. " +
                    "Functional success does not imply zero frame losses or a full two-minute replay."
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(folder, $"qp{qp}-trial-{runId}.json"), json);
            output.WriteLine(json);
            output.WriteLine(passed ? "quality trial: PASS" : "quality trial: FAIL");
            return passed ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"quality trial: FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            try
            {
                if (ObsHost.Started)
                    Obs.obs_set_output_source(0, IntPtr.Zero);
                if (source != IntPtr.Zero)
                    Obs.obs_source_release(source);
                pipeline?.Dispose();
            }
            finally
            {
                try
                {
                    ObsHost.Shutdown();
                }
                finally
                {
                    Directory.SetCurrentDirectory(workingDirectory);
                }
            }
        }
    }
}
