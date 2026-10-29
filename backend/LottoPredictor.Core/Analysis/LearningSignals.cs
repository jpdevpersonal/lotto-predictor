namespace LottoPredictor.Core.Analysis;

/// <summary>Dense, strategy-independent feedback for one predicted draw.
/// <para><b>ScoreZ</b> is the standardised sum of the drawn balls' scores. For balls drawn uniformly
/// without replacement it has mean 0 and variance 1 exactly, whatever the scores are. It is the
/// score-test statistic (the gradient at zero temperature) of the fixed-size-set log-likelihood
/// used by the backtest information test, so it measures whether a ranking carries information
/// without fitting a temperature.</para>
/// <para><b>Reward</b> = ScoreZ + λ·(I(3+) − p₃)/√(p₃(1−p₃)). Both terms have mean 0 under fair draws, so
/// the reward has mean 0 for every strategy unless the strategy has a real edge.</para></summary>
public static class LearningSignals
{
    public const double DefaultThreePlusWeight = 0.25;

    /// <summary>Scores for balls 1..N, rescaled to mean 0 and population variance 1.</summary>
    public static double[] Standardise(IReadOnlyDictionary<int, double> scores)
    {
        int max = scores.Count == 0 ? 0 : scores.Keys.Max();
        var values = new double[max];
        for (int n = 1; n <= max; n++) values[n - 1] = scores.GetValueOrDefault(n);
        if (max == 0) return values;
        double mean = values.Average();
        double std = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / max);
        for (int i = 0; i < max; i++) values[i] = std > 1e-12 ? (values[i] - mean) / std : 0;
        return values;
    }

    /// <summary>Standardised drawn-ball score sum; 0 when the ranking is flat.</summary>
    public static double ScoreZ(IReadOnlyList<double> standardisedScores, IEnumerable<int> drawn)
    {
        int n = standardisedScores.Count;
        if (n < 2) return 0;
        double mean = standardisedScores.Average();
        double var = standardisedScores.Sum(v => (v - mean) * (v - mean)) / n;
        if (var < 1e-12) return 0;
        double std = Math.Sqrt(var);

        double sum = 0;
        int k = 0;
        foreach (int ball in drawn.Distinct())
        {
            if (ball < 1 || ball > n) continue;
            sum += (standardisedScores[ball - 1] - mean) / std;
            k++;
        }
        if (k == 0 || k == n) return 0;
        double nullVariance = k * (n - k) / (double)(n - 1);
        return sum / Math.Sqrt(nullVariance);
    }

    public static double Reward(double scoreZ, bool threePlus, double randomThreePlusProbability,
        double threePlusWeight = DefaultThreePlusWeight)
    {
        double p = randomThreePlusProbability;
        double hitZ = p is > 0 and < 1 ? ((threePlus ? 1 : 0) - p) / Math.Sqrt(p * (1 - p)) : 0;
        return scoreZ + threePlusWeight * hitZ;
    }

    /// <summary>P(rate &lt; p) under the Beta(1+hits, 1+misses) posterior, using the identity
    /// Beta(a,b) CDF(p) = P(Binomial(a+b−1, p) ≥ a) for integer a, b.</summary>
    public static double ProbabilityRateBelow(int hits, int trials, double p)
    {
        int a = 1 + hits, b = 1 + (trials - hits);
        return StatFunctions.BinomialUpperTail(a + b - 1, a, p);
    }
}
