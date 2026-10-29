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

    /// <summary>Effective window cap for squared-gradient accumulation. Accumulates like AdaGrad
    /// during initial warmup, then transitions to an exponential moving average (RMSProp-style)
    /// so the effective learning rate never freezes on long draw histories.</summary>
    public const double MaxAccumulatorWindow = 80.0;

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
    public void Update(FeatureSet prefixFeatures, IReadOnlyCollection<int> drawn) =>
        Update(PredictionEngine.FeatureMatrix(prefixFeatures), drawn);

    /// <summary>One gradient step using a precomputed feature matrix from strictly earlier draws.</summary>
    public void Update((int[] Numbers, double[][] X) matrix, IReadOnlyCollection<int> drawn)
    {
        var (numbers, x) = matrix;
        if (numbers.Length == 0) return;
        var drawnSet = new HashSet<int>(drawn);
        int k = 0;
        for (int i = 0; i < numbers.Length; i++)
            if (drawnSet.Contains(numbers[i])) k++;
        if (k == 0) return;

        // Softmax over eligible balls. For k balls drawn without replacement, the k-draw softmax
        // gradient Σ_drawn x − k·E_p[x] is the standard approximation; it is exact at w = 0.
        var logits = new double[numbers.Length];
        double max = double.NegativeInfinity;
        for (int i = 0; i < numbers.Length; i++)
        {
            double l = 0;
            var xi = x[i];
            for (int j = 0; j < PredictionEngine.FeatureCount; j++)
                l += Weights[j] * xi[j];
            logits[i] = l;
            if (l > max) max = l;
        }
        double z = 0;
        var p = new double[numbers.Length];
        for (int i = 0; i < numbers.Length; i++) { p[i] = Math.Exp(logits[i] - max); z += p[i]; }
        for (int i = 0; i < numbers.Length; i++) p[i] /= z;

        var gradient = new double[PredictionEngine.FeatureCount];
        for (int i = 0; i < numbers.Length; i++)
        {
            double observed = drawnSet.Contains(numbers[i]) ? 1 : 0;
            double diff = observed - k * p[i];
            var xi = x[i];
            for (int j = 0; j < PredictionEngine.FeatureCount; j++)
                gradient[j] += diff * xi[j];
        }

        double decay = Updates < MaxAccumulatorWindow ? 1.0 : 1.0 - (1.0 / MaxAccumulatorWindow);
        for (int j = 0; j < PredictionEngine.FeatureCount; j++)
        {
            double g = gradient[j] - L2 * Weights[j];
            Accumulators[j] = decay * Accumulators[j] + g * g;
            Weights[j] += LearningRate * g / (Math.Sqrt(Accumulators[j]) + 1e-8);
        }
        Updates++;
    }
}
