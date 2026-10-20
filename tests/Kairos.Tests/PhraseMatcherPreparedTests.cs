using Kairos.Engine.Voice;

namespace Kairos.Tests;

public static class PhraseMatcherPreparedTests
{
    static float[][] Sequence(int length, int dimensions, Random random) =>
        [.. Enumerable.Range(0, length).Select(_ => Enumerable.Range(0, dimensions)
            .Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray())];

    static float[][] Copy(float[][] sequence) => [.. sequence.Select(frame => frame.ToArray())];

    // Kept independent of the production matcher: the original full-matrix recurrence and
    // normalization order are the reference for both legacy and prepared APIs.
    static double ReferenceCost(float[][] template, float[][] query)
    {
        int n = template.Length, m = query.Length;
        if (n == 0 || m < n / 2)
            return double.PositiveInfinity;
        static float[][] Normalize(float[][] sequence) => [.. sequence.Select(frame =>
        {
            double norm = 0;
            foreach (float value in frame)
                norm += value * value;
            float scale = (float)(1 / (Math.Sqrt(norm) + 1e-6));
            return frame.Select(value => value * scale).ToArray();
        })];
        static double Distance(float[] a, float[] b)
        {
            double dot = 0;
            for (int k = 0; k < a.Length; k++)
                dot += a[k] * b[k];
            return 1 - dot;
        }
        var t = Normalize(template);
        var q = Normalize(query);
        var d = new double[n, m];
        for (int j = 0; j < m; j++)
            d[0, j] = Distance(t[0], q[j]);
        for (int i = 1; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                double best = double.PositiveInfinity;
                if (j >= 1)
                {
                    best = Math.Min(best, d[i - 1, j - 1]);
                    if (i >= 2)
                        best = Math.Min(best, d[i - 2, j - 1]);
                }
                if (j >= 2)
                    best = Math.Min(best, d[i - 1, j - 2]);
                d[i, j] = double.IsPositiveInfinity(best) ? best : best + Distance(t[i], q[j]);
            }
        double end = double.PositiveInfinity;
        for (int j = 0; j < m; j++)
            end = Math.Min(end, d[n - 1, j]);
        return end / n;
    }

    static double ReferenceScore(IReadOnlyList<float[][]> templates, float[][] query)
    {
        double first = double.PositiveInfinity, second = double.PositiveInfinity;
        foreach (var template in templates)
        {
            double cost = ReferenceCost(template, query);
            if (cost < first)
            {
                second = first;
                first = cost;
            }
            else if (cost < second)
                second = cost;
        }
        return double.IsPositiveInfinity(second) ? first : (first + second) / 2;
    }

    [Test]
    public static void Rolling_rows_match_the_original_matrix_exactly_for_varied_sequences()
    {
        var random = new Random(9137);
        for (int trial = 0; trial < 350; trial++)
        {
            int dimensions = new[] { 0, 1, 7, 96 }[trial % 4];
            var template = Sequence(trial < 8 ? trial : random.Next(1, 64), dimensions, random);
            var query = Sequence(trial < 8 ? trial / 2 : random.Next(0, 75), dimensions, random);
            double expected = ReferenceCost(template, query);
            Assert.Equal(expected, PhraseMatcher.Cost(template, query), $"legacy cost at {trial}");
            Assert.Equal(expected, PhraseMatcher.Score(PhraseMatcher.PrepareTemplates([template]),
                PhraseMatcher.PrepareQuery(query)), $"prepared cost at {trial}");
        }
    }

    [Test]
    public static void Prepared_score_keeps_the_two_best_costs_and_empty_or_too_short_results()
    {
        var random = new Random(729);
        foreach (int count in new[] { 0, 1, 2, 3, 9 })
            foreach (int length in new[] { 0, 1, 4, 17, 48 })
            {
                float[][][] templates = [.. Enumerable.Range(0, count)
                    .Select(i => Sequence(i == 0 ? 0 : i * 3 + 1, 96, random))];
                var query = Sequence(length, 96, random);
                Assert.Equal(ReferenceScore(templates, query), PhraseMatcher.Score(templates, query), "legacy score");
                Assert.Equal(ReferenceScore(templates, query), PhraseMatcher.Score(
                    PhraseMatcher.PrepareTemplates(templates), PhraseMatcher.PrepareQuery(query)), "prepared score");
            }
    }

    [Test]
    public static void One_utterance_can_be_reused_across_ten_phrases_without_stale_rows()
    {
        var random = new Random(912);
        float[][][][] phrases = [.. Enumerable.Range(0, 10).Select(p => Enumerable.Range(0, 9)
            .Select(t => Sequence((p * 7 + t * 3) % 39 + 1, 12, random)).ToArray())];
        var prepared = phrases.Select(PhraseMatcher.PrepareTemplates).ToArray();
        foreach (int length in new[] { 1, 5, 43, 18 })
        {
            var query = Sequence(length, 12, random);
            var utterance = PhraseMatcher.PrepareQuery(query);
            var expected = phrases.Select(phrase => ReferenceScore(phrase, query)).ToArray();
            for (int repeat = 0; repeat < 3; repeat++)
                for (int p = 0; p < prepared.Length; p++)
                    Assert.Equal(expected[p], PhraseMatcher.Score(prepared[p], utterance), $"phrase {p}, repeat {repeat}");
        }
    }

    [Test]
    public static void Preparing_and_scoring_do_not_mutate_inputs_and_prepared_values_own_their_copies()
    {
        var random = new Random(5123);
        var template = Sequence(17, 96, random);
        var query = Sequence(39, 96, random);
        var originalTemplate = Copy(template);
        var originalQuery = Copy(query);
        var saved = PhraseMatcher.PrepareTemplates([template]);
        var utterance = PhraseMatcher.PrepareQuery(query);
        double expected = ReferenceCost(template, query);
        Assert.Equal(expected, PhraseMatcher.Score(saved, utterance), "original score");
        for (int i = 0; i < template.Length; i++)
            Assert.True(template[i].SequenceEqual(originalTemplate[i]), "template left unchanged");
        for (int i = 0; i < query.Length; i++)
            Assert.True(query[i].SequenceEqual(originalQuery[i]), "utterance left unchanged");
        foreach (var frame in template.Concat(query))
            Array.Fill(frame, 0);
        template[0] = [999];
        query[0] = [];
        Assert.Equal(expected, PhraseMatcher.Score(saved, utterance), "prepared snapshots survive caller mutations");
    }

    [Test]
    public static void Separate_workers_can_share_prepared_templates_with_independent_utterances()
    {
        var random = new Random(437);
        var templates = Enumerable.Range(0, 9).Select(i => Sequence(12 + i, 96, random)).ToArray();
        var saved = PhraseMatcher.PrepareTemplates(templates);
        var queries = Enumerable.Range(0, 12).Select(i => Sequence(20 + i, 96, random)).ToArray();
        var expected = queries.Select(query => ReferenceScore(templates, query)).ToArray();
        var results = queries.Select(query => Task.Run(() =>
        {
            var utterance = PhraseMatcher.PrepareQuery(query);
            double score = 0;
            for (int i = 0; i < 5; i++)
                score = PhraseMatcher.Score(saved, utterance);
            return score;
        })).ToArray();
        Task.WaitAll(results);
        for (int i = 0; i < results.Length; i++)
            Assert.Equal(expected[i], results[i].Result, $"worker {i}");
    }

    [Test]
    public static void Matching_ten_saved_phrases_allocates_no_per_template_workspace()
    {
        var random = new Random(2187);
        var phrases = Enumerable.Range(0, 10).Select(_ => PhraseMatcher.PrepareTemplates(
            Enumerable.Range(0, 9).Select(i => Sequence(16 + i, 96, random)).ToArray())).ToArray();
        var utterance = PhraseMatcher.PrepareQuery(Sequence(38, 96, random));
        foreach (var phrase in phrases)
            PhraseMatcher.Score(phrase, utterance);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double total = 0;
        for (int repeat = 0; repeat < 10; repeat++)
            foreach (var phrase in phrases)
                total += PhraseMatcher.Score(phrase, utterance);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(double.IsFinite(total), "all phrase costs are finite");
        Assert.Equal(0L, allocated, "ten phrases use the utterance's reusable rows");
    }
}
