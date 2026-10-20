using Kairos.App;

namespace Kairos.Tests;

/// <summary>The version as people read it: a beta says so in their language; anything else stays as it is.</summary>
public static class VersionTests
{
    [Test]
    public static void A_beta_reads_as_beta_n_in_every_language()
    {
        LanguageTests.WithLanguage("en", () =>
        {
            Assert.Equal("1.0.0 beta 3", AppInfo.Display("1.0.0-beta.3"), "English");
            Assert.Equal("1.0.0", AppInfo.Display("1.0.0"), "a release");
            Assert.Equal("1.2.0-rc.1", AppInfo.Display("1.2.0-rc.1"), "anything else as it is");
        });
        LanguageTests.WithLanguage("ar", () => Assert.Equal("1.0.0 تجريبي 3", AppInfo.Display("1.0.0-beta.3"), "Arabic"));
    }

    [Test]
    public static void The_version_is_the_full_one_without_build_details()
    {
        Assert.True(!AppInfo.Version.Contains('+'), AppInfo.Version);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(AppInfo.Version, @"^\d+\.\d+\.\d+(-(beta|rx)\.\d+)?$"), $"a release or preview version: {AppInfo.Version}");
    }
}
