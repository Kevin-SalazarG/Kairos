using System.Reflection;
using System.Text.RegularExpressions;

namespace Kairos;

/// <summary>Every name the app registers with Windows, in one place so a rename is a single edit.</summary>
public static class AppInfo
{
    public const string Name = "Kairos RX";
    /// <summary>The built executable and resource assembly retain their upstream file name.</summary>
    public const string ExecutableName = "Kairos";
    public const string EngineMutex = @"Local\KairosRX.Engine";
    public const string SettingsMutex = @"Local\KairosRX.Settings";
    public const string SettingsShowEvent = @"Local\KairosRX.Settings.Show";
    public const string PipeName = "KairosRX";
    public const string RunValueName = "KairosRX";

    /// <summary>The full version, a prerelease part included ("1.0.0-beta.1"), without the build details after "+".</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>A version as people read it: "1.0.0-beta.3" is "1.0.0 beta 3" in their language; any other stays as it is.</summary>
    public static string Display(string version) => Regex.Match(version, @"^(\d+\.\d+\.\d+)-beta\.(\d+)$") is { Success: true } m
        ? App.Text.Format("version.beta", m.Groups[1].Value, m.Groups[2].Value)
        : version;
}
