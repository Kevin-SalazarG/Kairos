namespace Kairos.Engine.Voice;

/// <summary>Compares sequences of speech embeddings with subsequence dynamic time warping.</summary>
public static class PhraseMatcher
{
    /// <summary>A normalized copy of the recorded templates, reusable across utterances.</summary>
    public sealed class PreparedTemplates
    {
        internal readonly float[][][] Sequences;

        internal PreparedTemplates(IReadOnlyList<float[][]> templates)
        {
            Sequences = new float[templates.Count][][];
            for (int i = 0; i < templates.Count; i++)
                Sequences[i] = Normalize(templates[i]);
        }
    }

    /// <summary>
    /// One normalized utterance and its three alignment rows. Reuse it sequentially across phrases;
    /// each listening worker prepares its own query rather than sharing a query between threads.
    /// </summary>
    public sealed class PreparedQuery
    {
        internal readonly float[][] Sequence;
        internal readonly double[] First, Second, Third;

        internal PreparedQuery(float[][] query)
        {
            Sequence = Normalize(query);
            First = new double[query.Length];
            Second = new double[query.Length];
            Third = new double[query.Length];
        }
    }

    public static PreparedTemplates PrepareTemplates(IReadOnlyList<float[][]> templates) => new(templates);
    public static PreparedQuery PrepareQuery(float[][] query) => new(query);

    /// <summary>
    /// Cheapest cosine-distance alignment of the whole <paramref name="template"/> to any stretch of
    /// <paramref name="query"/>, divided by the template length. Steps (1,1), (1,2) and (2,1) allow speaking
    /// up to twice as fast or slow without letting one frame absorb many.
    /// </summary>
    public static double Cost(float[][] template, float[][] query)
    {
        int n = template.Length, m = query.Length;
        if (n == 0 || m < n / 2)
            return double.PositiveInfinity;
        var t = Normalize(template);
        return Cost(t, PrepareQuery(query));
    }

    static double Cost(float[][] t, PreparedQuery query)
    {
        int n = t.Length, m = query.Sequence.Length;
        if (n == 0 || m < n / 2)
            return double.PositiveInfinity;
        var q = query.Sequence;
        var previous = query.First;
        var current = query.Second;
        var beforePrevious = query.Third;

        for (int j = 0; j < m; j++)
            previous[j] = Distance(t[0], q[j]);
        for (int i = 1; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                double best = double.PositiveInfinity;
                if (j >= 1)
                {
                    best = Math.Min(best, previous[j - 1]);
                    if (i >= 2)
                        best = Math.Min(best, beforePrevious[j - 1]);
                }
                if (j >= 2)
                    best = Math.Min(best, previous[j - 2]);
                current[j] = double.IsPositiveInfinity(best) ? best : best + Distance(t[i], q[j]);
            }
            (beforePrevious, previous, current) = (previous, current, beforePrevious);
        }

        double end = double.PositiveInfinity;
        for (int j = 0; j < m; j++)
            end = Math.Min(end, previous[j]);
        return end / n;
    }

    /// <summary>Mean cost of the two templates that fit best (one template: its cost).</summary>
    public static double Score(IReadOnlyList<float[][]> templates, float[][] query)
    {
        double first = double.PositiveInfinity, second = double.PositiveInfinity;
        PreparedQuery? preparedQuery = null;
        foreach (var t in templates)
        {
            if (t.Length == 0 || query.Length < t.Length / 2)
                continue;
            var normalized = Normalize(t);
            preparedQuery ??= PrepareQuery(query);
            double c = Cost(normalized, preparedQuery);
            if (c < first)
            {
                second = first;
                first = c;
            }
            else if (c < second)
            {
                second = c;
            }
        }
        return double.IsPositiveInfinity(second) ? first : (first + second) / 2;
    }

    /// <summary>Scores a saved phrase without normalizing its templates or allocating alignment matrices.</summary>
    public static double Score(PreparedTemplates templates, PreparedQuery query)
    {
        double first = double.PositiveInfinity, second = double.PositiveInfinity;
        foreach (var t in templates.Sequences)
        {
            double c = Cost(t, query);
            if (c < first)
            {
                second = first;
                first = c;
            }
            else if (c < second)
            {
                second = c;
            }
        }
        return double.IsPositiveInfinity(second) ? first : (first + second) / 2;
    }

    /// <summary>0..1 for the UI: full at 0.6× the threshold, 0.6 at the threshold, empty at 1.6×.</summary>
    public static double Strength(double score, double threshold) =>
        Math.Clamp((1.6 * threshold - score) / threshold, 0, 1);

    static float[][] Normalize(float[][] seq)
    {
        var result = new float[seq.Length][];
        for (int i = 0; i < seq.Length; i++)
        {
            double norm = 0;
            foreach (float v in seq[i])
                norm += v * v;
            float scale = (float)(1 / (Math.Sqrt(norm) + 1e-6));
            var frame = new float[seq[i].Length];
            for (int j = 0; j < frame.Length; j++)
                frame[j] = seq[i][j] * scale;
            result[i] = frame;
        }
        return result;
    }

    static double Distance(float[] a, float[] b)
    {
        double dot = 0;
        for (int k = 0; k < a.Length; k++)
            dot += a[k] * b[k];
        return 1 - dot;
    }
}
