using System.Diagnostics;
using System.Text.Json;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Explicit headless check of the RX 6600 profile; files and native configuration stay in its output folder.</summary>
public static class ProfileSmokeTest
{
    public static int Run(string[] args, TextWriter output)
    {
        CapturePipeline? pipeline = null;
        string workingDirectory = Directory.GetCurrentDirectory();
        using var recordingStopped = new ManualResetEventSlim();
        using var replaySaved = new ManualResetEventSlim();
        string? recordingPath = null, replayPath = null;
        double recordingClock = 0;
        try
        {
            string runId = Guid.NewGuid().ToString("N");
            string folder = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                ? Path.GetFullPath(args[1])
                : Path.Combine(Path.GetTempPath(), "kairos-rx6600-smoke", runId);
            int seconds = Math.Clamp(args.Length > 2 && int.TryParse(args[2], out int requested) ? requested : 12, 5, 120);
            Directory.CreateDirectory(folder);

            var settings = new AppSettings();
            CaptureProfiles.ApplyRx6600(settings);
            settings.Capture.Mode = CaptureMode.Screen;
            settings.Audio.System = false;
            settings.Audio.Mic = false;
            settings.Audio.SourceAudioOnly = false;
            settings.Audio.SeparateTracks = false;
            settings.Voice.Enabled = false;
            settings.General.OnlyWhenGaming = false;
            settings.Output.Folder = folder;
            settings.Normalize();

            ClipTranscoder.Load(AppContext.BaseDirectory);
            ObsHost.Start(AppContext.BaseDirectory, Path.Combine(folder, "config-" + runId),
                Path.Combine(folder, "logs-" + runId), new VideoConfig(1920, 1080, 1920, 1080, 60), VideoCodec.Hevc);
            ObsHost.LoadLater();
            output.WriteLine($"log: {ObsLog.CurrentPath}");
            if (!CaptureProfiles.IsRx6600Compatible(ObsHost.EncoderIds()))
                throw new InvalidOperationException("The AMD HEVC texture encoder required by the RX 6600 profile is unavailable.");

            pipeline = new CapturePipeline(settings);
            pipeline.Build();
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
            if (!pipeline.SetBufferActive(true))
                throw new InvalidOperationException($"Replay start failed: {pipeline.LastError}");

            string? screenshotPath = null;
            var screenshot = ScreenshotGrabber.Grab(pipeline.Base.W, pipeline.Base.H, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (screenshot != null)
                screenshotPath = ScreenshotWriter.Write(Path.Combine(folder, "native-" + runId + ".png"), screenshot);

            string target = Path.Combine(folder, "recording-" + runId + ".mp4");
            if (!pipeline.ToggleRecording(target))
                throw new InvalidOperationException($"Recording start failed: {pipeline.LastError}");

            using var process = Process.GetCurrentProcess();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            process.Refresh();
            double cpuPercent = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds /
                clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            EngineStatus status = pipeline.GetStatus();
            pipeline.ToggleRecording();
            if (!recordingStopped.Wait(TimeSpan.FromSeconds(20)) || recordingPath == null || !File.Exists(recordingPath))
                throw new InvalidOperationException("The recording did not finish within 20 seconds.");
            if (!pipeline.SaveReplay(false) || !replaySaved.Wait(TimeSpan.FromSeconds(30)) || replayPath == null || !File.Exists(replayPath))
                throw new InvalidOperationException("The replay did not save within 30 seconds.");

            MediaInfo recording = ClipTranscoder.Probe(recordingPath);
            MediaInfo replay = ClipTranscoder.Probe(replayPath);
            bool VideoMatches(MediaInfo info) => info.VideoCodec == "hevc" && info.Width == pipeline.Output.W &&
                info.Height == pipeline.Output.H && Math.Abs(info.Fps - 60) < 0.1;
            bool passed = pipeline.Choice?.Vendor == EncoderVendor.Amd && status.RateControl == "CQP" && status.QualityLevel == 16 &&
                VideoMatches(recording) && VideoMatches(replay) && Math.Abs(recording.Duration - seconds) <= Math.Max(1, seconds * 0.1) &&
                Math.Abs(recordingClock - seconds) <= 1 && replay.Duration > 2 && !status.FolderFellBack;

            string json = JsonSerializer.Serialize(new
            {
                Passed = passed,
                RequestedSeconds = seconds,
                RecordingClockSeconds = recordingClock,
                CpuPercent = cpuPercent,
                PrivateMemoryMb = process.PrivateMemorySize64 / (1024 * 1024),
                Status = status,
                FailedModules = ObsHost.FailedModules.ToArray(),
                RecordingPath = recordingPath,
                Recording = recording,
                ReplayPath = replayPath,
                Replay = replay,
                NativeScreenshotPath = screenshotPath,
                Note = "Frame losses are reported for diagnosis; functional success does not require zero losses."
            }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            File.WriteAllText(Path.Combine(folder, "profile-smoke-" + runId + ".json"), json);
            output.WriteLine(passed ? "profile smoke: PASS" : "profile smoke: FAIL");
            return passed ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"profile smoke: FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            try
            {
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
