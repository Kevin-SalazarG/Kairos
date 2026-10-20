using System.Buffers.Binary;
using System.Text;
using Kairos.Engine.Voice;

namespace Kairos.Tests;

public static class WavFileBoundsTests
{
    static byte[] Wav(Action<BinaryWriter> chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        chunks(writer);
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length - 8);
        return result;
    }

    static void Format(BinaryWriter writer, ushort channels = 1, ushort format = 1, ushort bits = 16,
        uint rate = 16000, ushort? alignment = null, uint? byteRate = null)
    {
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write(format);
        writer.Write(channels);
        writer.Write(rate);
        ushort align = alignment ?? (ushort)(channels * 2);
        writer.Write(byteRate ?? rate * align);
        writer.Write(align);
        writer.Write(bits);
    }

    static void Data(BinaryWriter writer, params short[] samples)
    {
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (short sample in samples)
            writer.Write(sample);
    }

    static T WithFile<T>(byte[] bytes, Func<string, T> read)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kairos-wav-bounds-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, bytes);
            return read(path);
        }
        finally { File.Delete(path); }
    }

    static void Reject(byte[] bytes, string description) => WithFile(bytes, path =>
    {
        try { WavFile.Read(path); }
        catch (InvalidDataException) { return true; }
        throw new Exception($"{description}: malformed WAV was accepted");
    });

    [Test]
    public static void Valid_mono_stereo_and_padded_metadata_keep_the_same_sample_values()
    {
        var mono = Wav(writer =>
        {
            writer.Write("JUNK"u8);
            writer.Write(1);
            writer.Write((byte)42);
            writer.Write((byte)0); // RIFF chunks are aligned to two bytes.
            Format(writer);
            Data(writer, short.MinValue, -1000, 0, 1000, short.MaxValue);
        });
        var decoded = WithFile(mono, WavFile.Read);
        Assert.True(decoded.SequenceEqual(new short[] { short.MinValue, -1000, 0, 1000, short.MaxValue }
            .Select(value => value / 32768f)), "mono values including signed extremes");
        var stereo = Wav(writer =>
        {
            Format(writer, channels: 2);
            Data(writer, -3000, 1000, 12000, 24000, short.MinValue, short.MaxValue);
        });
        Assert.True(WithFile(stereo, WavFile.Read).SequenceEqual(new[]
        {
            (-3000 + 1000) / (32768f * 2), (12000 + 24000) / (32768f * 2),
            (short.MinValue + short.MaxValue) / (32768f * 2),
        }), "stereo uses the original integer-sum downmix");
    }

    [Test]
    public static void Invalid_headers_formats_and_truncated_frames_are_rejected()
    {
        Reject([], "empty header");
        Reject("RIFF\0\0\0\0"u8.ToArray(), "short header");
        var valid = Wav(writer => { Format(writer); Data(writer, 1000); });
        var wrongRiff = valid.ToArray(); wrongRiff[0] = (byte)'X'; Reject(wrongRiff, "wrong RIFF signature");
        var wrongWave = valid.ToArray(); wrongWave[8] = (byte)'X'; Reject(wrongWave, "wrong WAVE signature");
        var longRiff = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(longRiff.AsSpan(4), uint.MaxValue);
        Reject(longRiff, "RIFF larger than file");
        var shortRiff = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(shortRiff.AsSpan(4), 3);
        Reject(shortRiff, "RIFF smaller than header");
        Reject(Wav(writer => Data(writer, 1000)), "missing format");
        Reject(Wav(writer => { Format(writer, channels: 0); Data(writer, 0); }), "zero channels");
        Reject(Wav(writer => { Format(writer, format: 3); Data(writer, 0); }), "non-PCM format");
        Reject(Wav(writer => { Format(writer, bits: 8); Data(writer, 0); }), "unsupported sample width");
        Reject(Wav(writer => { Format(writer, rate: 48000); Data(writer, 0); }), "unsupported sample rate");
        Reject(Wav(writer => { Format(writer, alignment: 1); Data(writer, 0); }), "invalid alignment");
        Reject(Wav(writer => { Format(writer, byteRate: 1); Data(writer, 0); }), "invalid byte rate");
        Reject(Wav(writer => { Format(writer, channels: 2); Data(writer, 1); }), "incomplete stereo frame");
        Reject(Wav(writer =>
        {
            Format(writer);
            writer.Write("data"u8); writer.Write(1); writer.Write((byte)0); writer.Write((byte)0);
        }), "incomplete 16-bit sample");
        Reject(Wav(writer =>
        {
            writer.Write("fmt "u8); writer.Write(12); writer.Write(new byte[12]);
        }), "format chunk smaller than PCM header");
        Reject(Wav(writer => { Format(writer); writer.Write((byte)0); }), "incomplete chunk header");
    }

    [Test]
    public static void Forged_chunk_sizes_cannot_seek_backwards_or_allocate_from_untrusted_lengths()
    {
        foreach (string chunk in new[] { "data", "JUNK", "fmt " })
            foreach (uint size in new[] { (uint)int.MaxValue, uint.MaxValue, 0xfffffff8U })
            {
                var file = Wav(writer =>
                {
                    Format(writer);
                    writer.Write(Encoding.ASCII.GetBytes(chunk));
                    writer.Write(size);
                });
                WithFile(file, path =>
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    bool rejected = false;
                    try { WavFile.Read(path); }
                    catch (InvalidDataException) { rejected = true; }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.True(rejected, $"{chunk} size {size} rejected");
                    Assert.True(allocated < 65536, $"a tiny damaged file caused only {allocated} bytes of allocation");
                    return true;
                });
            }
    }
}
