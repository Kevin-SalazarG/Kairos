namespace Kairos.Engine.Voice;

/// <summary>Streaming 48 kHz → 16 kHz converter: windowed-sinc low-pass at 7.2 kHz, then every third sample.</summary>
public sealed class Resampler48To16
{
    const int Taps = 127;
    static readonly float[] Kernel = BuildKernel();

    readonly float[] _history = new float[Taps - 1];
    float[] _buffer = [];
    int _next = Taps - 1;

    public float[] Process(ReadOnlySpan<float> input)
    {
        // Reuse staging and history across libobs's short audio blocks. Only the returned samples
        // need their own array: callers can keep them after the next block has been processed.
        int length = _history.Length + input.Length;
        if (_buffer.Length < length)
            _buffer = new float[length];
        var x = _buffer;
        _history.CopyTo(x, 0);
        input.CopyTo(x.AsSpan(_history.Length));

        var output = new float[Math.Max(0, (length - _next + 2) / 3)];
        int count = 0, p = _next;
        for (; p < length; p += 3)
        {
            float acc = 0;
            for (int k = 0; k < Taps; k++)
                acc += Kernel[k] * x[p - k];
            output[count++] = acc;
        }
        _next = p - input.Length;
        x.AsSpan(length - _history.Length, _history.Length).CopyTo(_history);
        return count == output.Length ? output : output[..count];
    }

    static float[] BuildKernel()
    {
        const double cutoff = 7200.0 / 48000.0;
        var h = new double[Taps];
        double m = Taps - 1, sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            double t = i - m / 2;
            double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            double blackman = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m);
            h[i] = sinc * blackman;
            sum += h[i];
        }
        return [.. h.Select(v => (float)(v / sum))];
    }
}
