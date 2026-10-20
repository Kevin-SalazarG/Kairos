namespace Kairos.Engine.Voice;

/// <summary>Minimal 16 kHz mono 16-bit PCM WAV reader and writer for the enrollment samples.</summary>
public static class WavFile
{
    public const int SampleRate = 16000;

    public static float[] Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        try
        {
            if (r.BaseStream.Length < 12 || r.ReadUInt32() != 0x46464952) // RIFF
                throw new InvalidDataException("not a WAV file");
            long end = r.ReadUInt32() + 8L;
            if (r.ReadUInt32() != 0x45564157 || end < 12 || end > r.BaseStream.Length) // WAVE
                throw new InvalidDataException("invalid WAV bounds");
            int channels = 0, blockAlign = 0;
            while (r.BaseStream.Position < end)
            {
                if (end - r.BaseStream.Position < 8)
                    throw new InvalidDataException("truncated WAV chunk header");
                uint id = r.ReadUInt32(), size = r.ReadUInt32();
                long next = r.BaseStream.Position + size + (size & 1);
                if (next > end)
                    throw new InvalidDataException("truncated WAV chunk");
                if (id == 0x20746d66) // fmt
                {
                    if (size < 16 || channels != 0)
                        throw new InvalidDataException("invalid WAV format chunk");
                    int format = r.ReadUInt16();
                    channels = r.ReadUInt16();
                    uint rate = r.ReadUInt32(), byteRate = r.ReadUInt32();
                    blockAlign = r.ReadUInt16();
                    int bits = r.ReadUInt16();
                    if (format != 1 || channels == 0 || bits != 16 || rate != SampleRate
                        || blockAlign != channels * 2 || byteRate != rate * blockAlign)
                        throw new InvalidDataException($"expected 16-bit {SampleRate} Hz PCM WAV");
                }
                else if (id == 0x61746164) // data
                {
                    if (channels == 0 || size % blockAlign != 0 || size / blockAlign > Array.MaxLength)
                        throw new InvalidDataException("invalid WAV sample bounds");
                    int frames = (int)(size / blockAlign);
                    var samples = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        int sum = 0;
                        for (int c = 0; c < channels; c++)
                            sum += r.ReadInt16();
                        samples[i] = sum / (32768f * channels);
                    }
                    return samples;
                }
                r.BaseStream.Position = next;
            }
            throw new InvalidDataException("WAV file has no data");
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("truncated WAV file", e);
        }
    }

    public static void Write(string path, ReadOnlySpan<float> samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (float s in samples)
            w.Write((short)Math.Clamp(Math.Round(s * 32767), short.MinValue, short.MaxValue));
    }
}
