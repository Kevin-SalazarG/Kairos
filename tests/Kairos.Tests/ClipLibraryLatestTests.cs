using Kairos.Engine;

namespace Kairos.Tests;

public static class ClipLibraryLatestTests
{
    static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0);

    static string Put(string root, string relative, int minutes)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        File.SetLastWriteTime(path, Start.AddMinutes(minutes));
        return path;
    }

    [Test]
    public static void Latest_lookups_keep_videos_and_pictures_separate()
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-latest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Put(root, @"Game\old.mp4", 1);
            string video = Put(root, @"Game\Highlights\new.MP4", 4);
            Put(root, "old.png", 2);
            string picture = Put(root, @"Screenshots\new.JPEG", 5);
            Put(root, "unrelated.mkv", 9);

            Assert.Equal(video, ClipLibrary.NewestVideo(root), "a newer picture cannot replace the newest video");
            Assert.Equal(picture, ClipLibrary.NewestPicture(root), "picture formats remain case insensitive");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public static void Latest_lookups_obey_the_library_hidden_and_depth_filters()
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-latest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string deepest = Path.Combine(Enumerable.Repeat("level", 8).ToArray());
            string video = Put(root, Path.Combine(deepest, "visible.mp4"), 1);
            string picture = Put(root, Path.Combine(deepest, "visible.jpg"), 2);
            Put(root, Path.Combine(deepest, "too-deep", "newer.mp4"), 9);
            Put(root, Path.Combine(deepest, "too-deep", "newer.jpg"), 9);
            Put(root, @".cache\newer.mp4", 9);
            Put(root, @".cache\newer.jpg", 9);
            Put(root, ".hidden.mp4", 9);
            Put(root, ".hidden.png", 9);
            File.SetAttributes(Put(root, "hidden.mp4", 9), FileAttributes.Hidden);
            File.SetAttributes(Put(root, "system.png", 9), FileAttributes.System);
            string hiddenDirectory = Path.GetDirectoryName(Put(root, @"Hidden\newer.mp4", 9))!;
            Put(root, @"Hidden\newer.jpg", 9);
            File.SetAttributes(hiddenDirectory, FileAttributes.Directory | FileAttributes.Hidden);

            Assert.Equal(video, ClipLibrary.NewestVideo(root), "depth eight is included, hidden and deeper clips are skipped");
            Assert.Equal(picture, ClipLibrary.NewestPicture(root), "pictures use the same traversal policy");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public static void Equal_timestamps_keep_the_same_first_file_as_the_library()
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-latest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Put(root, @"Other\first.mp4", 1);
            Put(root, "second.mp4", 1);
            Put(root, @"Other\first.png", 1);
            Put(root, "second.png", 1);
            var listed = ClipLibrary.Scan(root);

            Assert.Equal(listed.First(c => c.Kind == ClipKind.Video).Path, ClipLibrary.NewestVideo(root), "stable video ties");
            Assert.Equal(listed.First(c => c.Kind == ClipKind.Picture).Path, ClipLibrary.NewestPicture(root), "stable picture ties");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public static void Missing_or_empty_libraries_have_no_latest_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-latest-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(null, ClipLibrary.NewestVideo(root));
        Assert.Equal(null, ClipLibrary.NewestPicture(root));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(null, ClipLibrary.NewestVideo(root));
            Assert.Equal(null, ClipLibrary.NewestPicture(root));
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
