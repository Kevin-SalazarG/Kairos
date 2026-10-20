using System.Text.Json;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Exercises native build failures and recovery without touching the user's settings or running engine.</summary>
public static class PipelineRecoveryProbe
{
    public static int Run(string[] args, TextWriter output)
    {
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                output.WriteLine("usage: --selftest-recovery <isolated-output-folder>");
                return 2;
            }
            string folder = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(folder);
            var settings = new AppSettings();
            CaptureProfiles.ApplyRx6600DetailTrial(settings);
            settings.Capture.Mode = CaptureMode.Screen;
            settings.Audio.System = settings.Audio.Mic = settings.Audio.SourceAudioOnly = false;
            settings.Voice.Enabled = false;
            settings.General.OnlyWhenGaming = false;
            settings.Output.Folder = folder;
            settings.Normalize();
            ObsHost.Start(AppContext.BaseDirectory, Path.Combine(folder, "config"), Path.Combine(folder, "logs"),
                new VideoConfig(1920, 1080, 1920, 1080, 60), VideoCodec.Hevc);
            ObsHost.LoadLater();
            var encoders = ObsHost.EncoderIds();
            if (!CaptureProfiles.IsRx6600Compatible(encoders))
                throw new InvalidOperationException("This hardware check requires AMD HEVC.");
            const string failure = "Injected failure after native capture sources were created.";
            var attempts = new List<object>();
            bool clean = true;
            for (int i = 0; i < 3; i++)
            {
                bool expectedFailure = false;
                List<string> afterFailure;
                using (var failing = new CapturePipeline(settings)
                {
                    BeforeEncoders = () => throw new InvalidOperationException(failure)
                })
                {
                    try
                    {
                        failing.Build();
                    }
                    catch (InvalidOperationException e) when (e.Message == failure)
                    {
                        expectedFailure = true;
                    }
                    ObsHost.DrainDestroyQueue();
                    afterFailure = ObsHost.AliveSources();
                }
                var afterDispose = ObsHost.AliveSources();
                clean &= expectedFailure && afterFailure.Count == 0 && afterDispose.Count == 0;
                attempts.Add(new { ExpectedBuildFailure = expectedFailure, AfterFailure = afterFailure, AfterDispose = afterDispose });
            }
            EngineStatus status;
            using (var recovered = new CapturePipeline(settings))
            {
                recovered.Build();
                if (!recovered.SetBufferActive(true))
                    throw new InvalidOperationException(recovered.LastError);
                Thread.Sleep(3000);
                status = recovered.GetStatus();
                clean &= status.BufferActive && status.Encoder == "h265_texture_amf" &&
                    status.RateControl == "CQP" && status.QualityLevel == 14 &&
                    status.Width == 1920 && status.Height == 1080 && status.TotalFrames >= 60;
            }
            var finalSources = ObsHost.AliveSources();
            clean &= finalSources.Count == 0;
            string json = JsonSerializer.Serialize(new
            {
                Passed = clean, InjectedFailure = failure, Attempts = attempts,
                RecoveredStatus = status, FinalSources = finalSources,
                Note = "Native cleanup/recovery check; it does not measure game FPS or visual quality."
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(folder, "recovery.json"), json);
            output.WriteLine(json);
            output.WriteLine(clean ? "pipeline recovery: PASS" : "pipeline recovery: FAIL");
            return clean ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"pipeline recovery: FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            try { ObsHost.Shutdown(); }
            finally { Directory.SetCurrentDirectory(previousDirectory); }
        }
    }
}
