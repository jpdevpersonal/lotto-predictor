namespace LottoPredictor.Core.Analysis;

/// <summary>One scored shadow line, reduced to what prospective evaluation needs.</summary>
public sealed record ProspectiveOutcome(
    string StrategyName,
    string Kind,
    int CutoffSequence,
    int Matches,
    bool ThreePlus,
    double ScoreZ,
    double Reward,
    double RandomThreePlusProbability,
    double RandomExpectedMatches);

/// <summary>Forward-only track record of one strategy.</summary>
public sealed record ProspectiveRecord(
    string StrategyName,
    string Kind,
    int Draws,
    int ThreePlusHits,
    double ExpectedThreePlusHits,
    double MeanMatches,
    double MeanExpectedMatches,
    double MeanScoreZ,
    double MeanReward,
    double RewardStd,
    IReadOnlyDictionary<int, double> RewardByCutoff)
{
    public double ThreePlusRate => Draws > 0 ? (double)ThreePlusHits / Draws : 0;
    public double RandomThreePlusRate => Draws > 0 ? ExpectedThreePlusHits / Draws : 0;
    /// <summary>Mean score z × √n: ~N(0,1) under fair draws.</summary>
    public double ScoreZTotal => Draws > 0 ? MeanScoreZ * Math.Sqrt(Draws) : 0;

    /// <summary>Mean reward shrunk toward 0 (the fair-machine value) by pseudo-draws.</summary>
    public double ShrunkReward(double pseudoDraws) => MeanReward * Draws / (Draws + pseudoDraws);
}

public sealed record SelectionDecision(
    string ActiveName,
    string Source,
    string Reason,
    double? Confidence,
    int ProspectiveDraws,
    int PromotedAtSequence);

public sealed record SelectionSettings(
    int MinProspectiveDraws,
    double PromotionConfidence,
    int MinHoldDraws);

public static class ProspectiveEvaluator
{
    public const string SourceBacktest = "backtest";
    public const string SourceProspective = "prospective";

    public static IReadOnlyList<ProspectiveRecord> Summarise(IEnumerable<ProspectiveOutcome> outcomes) =>
        outcomes
            .GroupBy(o => o.StrategyName)
            .Select(g =>
            {
                var list = g.OrderBy(o => o.CutoffSequence).ToList();
                double meanReward = list.Average(o => o.Reward);
                double std = list.Count > 1
                    ? Math.Sqrt(list.Sum(o => (o.Reward - meanReward) * (o.Reward - meanReward)) / (list.Count - 1))
                    : 0;
                return new ProspectiveRecord(
                    g.Key,
                    list[^1].Kind,
                    list.Count,
                    list.Count(o => o.ThreePlus),
                    list.Sum(o => o.RandomThreePlusProbability),
                    list.Average(o => o.Matches),
                    list.Average(o => o.RandomExpectedMatches),
                    list.Average(o => o.ScoreZ),
                    meanReward,
                    std,
                    list.GroupBy(o => o.CutoffSequence).ToDictionary(c => c.Key, c => c.First().Reward));
            })
            .OrderByDescending(r => r.MeanReward)
            .ThenBy(r => r.StrategyName)
            .ToList();

    /// <summary>One-sided confidence that the challenger's reward exceeds the incumbent's, using
    /// a paired test over the draws both were scored on. Null when too few common draws.</summary>
    public static (double Confidence, int Common)? PairedConfidence(
        ProspectiveRecord challenger, ProspectiveRecord incumbent, int minCommon)
    {
        var diffs = challenger.RewardByCutoff
            .Where(kv => incumbent.RewardByCutoff.ContainsKey(kv.Key))
            .Select(kv => kv.Value - incumbent.RewardByCutoff[kv.Key])
            .ToList();
        if (diffs.Count < Math.Max(2, minCommon)) return null;
        double mean = diffs.Average();
        double sd = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / (diffs.Count - 1));
        double se = sd / Math.Sqrt(diffs.Count);
        if (se < 1e-12) return (mean > 0 ? 1 : mean < 0 ? 0 : 0.5, diffs.Count);
        return (StatFunctions.NormalCdf(mean / se), diffs.Count);
    }

    /// <summary>Backtest first; once enough prospective draws exist, keep the incumbent (the
    /// hedge ensemble by default) unless a challenger beats it with Bonferroni-corrected
    /// confidence and its 3+ rate is not credibly below random. Promotions are held for
    /// MinHoldDraws to avoid flip-flopping on noise.</summary>
    public static SelectionDecision Choose(
        string backtestChoice,
        IReadOnlyList<ProspectiveRecord> records,
        string? currentName,
        string? currentSource,
        int promotedAtSequence,
        int latestSequence,
        SelectionSettings settings)
    {
        int ledgerDraws = records.Count == 0
            ? 0
            : records.SelectMany(r => r.RewardByCutoff.Keys).Distinct().Count();
        if (ledgerDraws < settings.MinProspectiveDraws)
            return new SelectionDecision(
                backtestChoice, SourceBacktest,
                $"{ledgerDraws} of {settings.MinProspectiveDraws} prospective draws scored; " +
                "using the walk-forward backtest choice until real results accumulate.",
                null, ledgerDraws, 0);

        var byName = records.ToDictionary(r => r.StrategyName);
        string incumbent = currentSource == SourceProspective && currentName is not null && byName.ContainsKey(currentName)
            ? currentName
            : Backtester.EnsembleName;
        if (!byName.TryGetValue(incumbent, out var incumbentRecord))
            return new SelectionDecision(
                backtestChoice, SourceBacktest,
                "The hedge ensemble has no prospective record yet; using the backtest choice.",
                null, ledgerDraws, 0);
        int heldFrom = currentSource == SourceProspective && currentName == incumbent
            ? promotedAtSequence
            : latestSequence;

        if (currentSource == SourceProspective && currentName == incumbent &&
            incumbent != Backtester.EnsembleName &&
            latestSequence - promotedAtSequence < settings.MinHoldDraws)
            return new SelectionDecision(
                incumbent, SourceProspective,
                $"'{incumbent}' was promoted {latestSequence - promotedAtSequence} draw(s) ago and is held for " +
                $"at least {settings.MinHoldDraws} draws.",
                null, ledgerDraws, promotedAtSequence);

        var contenders = records
            .Where(r => r.StrategyName != incumbent && r.Draws >= settings.MinProspectiveDraws)
            .ToList();
        double threshold = 1 - (1 - settings.PromotionConfidence) / Math.Max(1, contenders.Count);

        (ProspectiveRecord Record, double Confidence)? best = null;
        foreach (var c in contenders)
        {
            var paired = PairedConfidence(c, incumbentRecord, settings.MinProspectiveDraws);
            if (paired is null) continue;
            if (best is null || paired.Value.Confidence > best.Value.Confidence)
                best = (c, paired.Value.Confidence);
        }

        if (best is { } winner && winner.Confidence >= threshold)
        {
            var w = winner.Record;
            double belowRandom = LearningSignals.ProbabilityRateBelow(
                w.ThreePlusHits, w.Draws, w.RandomThreePlusRate);
            if (belowRandom < settings.PromotionConfidence)
                return new SelectionDecision(
                    w.StrategyName, SourceProspective,
                    $"Promoted '{w.StrategyName}' over '{incumbent}': paired prospective reward confidence " +
                    $"{winner.Confidence:0.0000} ≥ {threshold:0.0000} (Bonferroni over {contenders.Count}), " +
                    $"{w.ThreePlusHits} 3+ hit(s) in {w.Draws} draws.",
                    winner.Confidence, ledgerDraws, latestSequence);
        }

        string bestText = best is { } b
            ? $" Best challenger '{b.Record.StrategyName}' reached {b.Confidence:0.0000} (needs {threshold:0.0000})."
            : "";
        return new SelectionDecision(
            incumbent, SourceProspective,
            $"No strategy beats '{incumbent}' on {ledgerDraws} prospective draws with corrected confidence.{bestText}",
            best?.Confidence, ledgerDraws, heldFrom);
    }
}
