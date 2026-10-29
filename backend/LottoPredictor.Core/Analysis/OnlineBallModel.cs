namespace LottoPredictor.Core.Analysis;

/// <summary>Online per-ball conditional-logit model. A ball's score is w·x, with x taken from
/// <see cref="PredictionEngine.FeatureMatrix"/>, so the model is itself a <see cref="ScoringStrategy"/>
/// whose weights are learned by gradient steps instead of genetic search.
/// <para>After each draw, one AdaGrad step is taken on the log-likelihood of the drawn balls, using
/// features computed only from earlier draws. L2 shrinkage pulls weights toward 0, which is the
/// fair-machine prior. On fair data the weights stay near 0; that is the evidence that there is no
/// signal.</para></summary>
public sealed class OnlineBallModel
{
    public const string StrategyName = "online-logit";

    public double[] Weights { get; }
    public double[] Accumulators { get; }
    public int Updates { get; private set; }
    public double LearningRate { get; }
    public double L2 { get; }

    public OnlineBallModel(double learningRate = 0.05, double l2 = 0.01,
        double[]? weights = null, double[]? accumulators = null, int updates = 0)
    {
        LearningRate = learningRate;
        L2 = l2;
        Weights = weights is { Length: PredictionEngine.FeatureCount }
            ? (double[])weights.Clone() : new double[PredictionEngine.FeatureCount];
        Accumulators = accumulators is { Length: PredictionEngine.FeatureCount }
            ? (double[])accumulators.Clone() : new double[PredictionEngine.FeatureCount];
        Updates = updates;
    }

    /// <summary>Pair and penalty weights match the hand-written strategies' defaults.</summary>
    public ScoringStrategy ToStrategy() => new(
        StrategyName,
        Math.Round(Weights[0], 6), Math.Round(Weights[1], 6), Math.Round(Weights[2], 6),
        Math.Round(Weights[3], 6), 0.25, 1.0,
        Math.Round(Weights[4], 6), Math.Round(Weights[5], 6));

    /// <summary>One gradient step. <paramref name="prefixFeatures"/> must be computed from draws
    /// strictly before the draw whose numbers are <paramref name="drawn"/>.</summary>
    public void Update(FeatureSet prefixFeatures, IReadOnlyCollection<int> drawn)
    {
        var (numbers, x) = PredictionEngine.FeatureMatrix(prefixFeatures);
        if (numbers.Length == 0) return;
        var drawnSet = new HashSet<int>(drawn);
        int k = numbers.Count(drawnSet.Contains);
        if (k == 0) return;

        // Softmax over eligible balls. For k balls drawn without replacement, the k-draw softmax
        // gradient Σ_drawn x − k·E_p[x] is the standard approximation; it is exact at w = 0.
        var logits = new double[numbers.Length];
        for (int i = 0; i < numbers.Length; i++)
            for (int j = 0; j < PredictionEngine.FeatureCount; j++)
                logits[i] += Weights[j] * x[i][j];
        double max = logits.Max();
        double z = 0;
        var p = new double[numbers.Length];
        for (int i = 0; i < numbers.Length; i++) { p[i] = Math.Exp(logits[i] - max); z += p[i]; }
        for (int i = 0; i < numbers.Length; i++) p[i] /= z;

        var gradient = new double[PredictionEngine.FeatureCount];
        for (int i = 0; i < numbers.Length; i++)
        {
            double observed = drawnSet.Contains(numbers[i]) ? 1 : 0;
            for (int j = 0; j < PredictionEngine.FeatureCount; j++)
                gradient[j] += (observed - k * p[i]) * x[i][j];
        }

        for (int j = 0; j < PredictionEngine.FeatureCount; j++)
        {
            double g = gradient[j] - L2 * Weights[j];
            Accumulators[j] += g * g;
            Weights[j] += LearningRate * g / (Math.Sqrt(Accumulators[j]) + 1e-8);
        }
        Updates++;
    }
}
