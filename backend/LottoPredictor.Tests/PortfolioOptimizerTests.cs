using LottoPredictor.Core.Analysis;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

public class PortfolioOptimizerTests
{
    [Fact]
    public void Coverage_portfolio_returns_distinct_fixed_k_lines()
    {
        var history = RandomHistory(260, 59, seed: 123);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 12);

        Assert.Equal(12, portfolio.Lines.Count);
        Assert.Equal(12, portfolio.Lines.Select(line => string.Join(',', line.Numbers)).Distinct().Count());
        Assert.All(portfolio.Lines, line => Assert.Equal(6, line.Numbers.Length));
        Assert.All(portfolio.Lines, line => Assert.Equal(line.Numbers, line.Numbers.OrderBy(number => number)));
    }

    [Fact]
    public void Coverage_portfolio_improves_or_matches_random_distinct_four_plus_estimate()
    {
        var history = RandomHistory(260, 59, seed: 456);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 25);

        Assert.True(portfolio.CoverageOptimized.Probability >= portfolio.RandomDistinct.Probability * 0.95);
        Assert.True(portfolio.CoverageOptimized.Probability >= portfolio.SingleLineFourPlusProbability);
    }

    [Fact]
    public void Five_number_games_are_generated_uniformly_at_their_actual_pick_count()
    {
        var history = RandomHistory(100, 50, seed: 789, pickCount: 5);

        Assert.All(history, draw => Assert.Equal(5, draw.Numbers.Length));
        Assert.Contains(history, draw => draw.Numbers.Any(number => number > 5));
    }

    [Fact]
    public void Coverage_portfolio_rejects_more_lines_than_the_legal_pool_can_supply()
    {
        var history = RandomHistory(60, 6, seed: 321);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 6);
        var strategy = ScoringStrategy.Candidates[0];

        Assert.Throws<InvalidOperationException>(() => PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 2));
    }
}
