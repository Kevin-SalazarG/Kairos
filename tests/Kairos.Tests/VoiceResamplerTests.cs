using Kairos.Engine.Voice;

namespace Kairos.Tests;

public static class VoiceResamplerTests
{
    [Test]
    public static void Resampler_preserves_samples_across_changing_block_sizes()
    {
        var random = new Random(8171);
        float[] audio = [.. Enumerable.Range(0, 24001).Select(_ => (float)(random.NextDouble() * 2 - 1))];
        float[] expected = new Resampler48To16().Process(audio);
        var streaming = new Resampler48To16();
        var actual = new List<float>();
        int[] sizes = [1, 2, 3, 479, 480, 481, 17, 1537, 2, 48, 4096, 3, 19];
        int offset = 0, block = 0;
        while (offset < audio.Length)
        {
            Assert.Equal(0, streaming.Process(ReadOnlySpan<float>.Empty).Length, "empty blocks preserve state");
            int count = Math.Min(sizes[block++ % sizes.Length], audio.Length - offset);
            actual.AddRange(streaming.Process(audio.AsSpan(offset, count)));
            offset += count;
        }
        Assert.Equal(expected.Length, actual.Count, "partitioning must preserve the decimation phase");
        Assert.True(expected.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(actual)),
            "every sample must be identical with reused buffers, including after shrinking a block");
    }

    [Test]
    public static void Resampler_outputs_remain_owned_by_the_caller()
    {
        float[] audio = [.. Enumerable.Range(0, 480).Select(i => (float)Math.Sin(i * 0.13))];
        var streaming = new Resampler48To16();
        float[] first = streaming.Process(audio);
        float[] original = [.. first];
        for (int i = 0; i < 20; i++)
            streaming.Process(new float[i % 2 == 0 ? 1537 : 17]);
        Assert.True(first.AsSpan().SequenceEqual(original), "later blocks must not overwrite a previous output");

        float[] fromAnotherStream = new Resampler48To16().Process(audio);
        Assert.True(first.AsSpan().SequenceEqual(fromAnotherStream), "resamplers must keep separate history");
    }

    [Test]
    public static void Resampler_steady_audio_blocks_stay_within_the_allocation_budget()
    {
        var streaming = new Resampler48To16();
        var audio = new float[480];
        for (int i = 0; i < 100; i++)
            streaming.Process(audio);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            streaming.Process(audio);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024L * 1000,
            $"short audio blocks should allocate only their returned samples, got {allocated} bytes");
    }
}
