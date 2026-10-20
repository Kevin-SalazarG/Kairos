using Kairos.App;
using Kairos.Settings;

namespace Kairos.Tests;

public static class ForkIdentityTests
{
    [Test]
    public static void Fork_identity_is_distinct_from_upstream()
    {
        Assert.Equal("Kairos RX", AppInfo.Name, "fork display and data-folder name");
        Assert.Equal(@"Local\KairosRX.Engine", AppInfo.EngineMutex, "engine mutex");
        Assert.Equal(@"Local\KairosRX.Settings", AppInfo.SettingsMutex, "settings mutex");
        Assert.Equal(@"Local\KairosRX.Settings.Show", AppInfo.SettingsShowEvent, "settings activation event");
        Assert.Equal("KairosRX", AppInfo.PipeName, "fork pipe");
        Assert.Equal("KairosRX", AppInfo.RunValueName, "fork startup entry");
        Assert.Equal(AppInfo.PipeName, Ipc.PipeName, "IPC uses the fork identity");
        Assert.True(AppInfo.EngineMutex != @"Local\Kairos.Engine", "must not acquire the upstream engine lock");
        Assert.True(AppInfo.SettingsMutex != @"Local\Kairos.Settings", "must not acquire the upstream settings lock");
        Assert.True(AppInfo.SettingsShowEvent != @"Local\Kairos.Settings.Show", "must not activate upstream settings");
        Assert.True(AppInfo.PipeName != "Kairos", "must not send commands to the upstream engine");
        Assert.True(AppInfo.RunValueName != "Kairos", "must not replace the upstream startup entry");
    }

    [Test]
    public static void Settings_and_default_clips_use_the_fork_folder()
    {
        string forkData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairos RX");
        string upstreamData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairos");
        string expectedData = Environment.GetEnvironmentVariable("KAIROS_DATA") is { Length: > 0 } isolatedData
            ? isolatedData
            : forkData;

        Assert.Equal(expectedData, SettingsStore.AppDataDir, "fork data folder or explicit development override");
        Assert.Equal(Path.Combine(expectedData, "settings.json"), SettingsStore.DefaultPath, "settings path");
        Assert.True(!Path.GetFullPath(forkData).Equals(Path.GetFullPath(upstreamData), StringComparison.OrdinalIgnoreCase),
            "default fork settings must be separate from upstream settings");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kairos RX"),
            OutputSettings.DefaultFolder, "default clips folder");
    }

    [Test]
    public static void Resource_assembly_identity_is_preserved()
    {
        Assert.Equal("Kairos", typeof(AppInfo).Assembly.GetName().Name, "existing WPF pack resources keep working");
        Assert.Equal("Kairos", AppInfo.ExecutableName, "executable file name");
        Assert.Equal(Path.Combine(@"C:\Kairos RX", "Kairos.exe"), InstallPaths.StubFor(@"C:\Kairos RX"),
            "installed launch stub keeps its packaged executable name");
    }
}
