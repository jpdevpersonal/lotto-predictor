namespace LottoPredictor.Core.Services;

using LottoPredictor.Core.Analysis;

public sealed record LotteryProfile(
    string Key,
    string Name,
    int MainNumberCount,
    int MainPoolSize,
    int BonusNumberCount,
    int BonusPoolSize,
    int RoundCount,
    DateOnly? MainPoolExpansionDate = null,
    /// <summary>True when the bonus ball is drawn from the same pool as the main numbers
    /// (UK Lotto). False when it has its own separate pool (EuroMillions Lucky Stars,
    /// Set For Life's Life Ball).</summary>
    bool BonusSharesMainPool = false,
    IReadOnlyList<PoolRule>? MainPoolRules = null,
    IReadOnlyList<PoolRule>? BonusPoolRules = null)
{
    public static LotteryProfile UkLotto { get; } = new(
        "uk-lotto", "UK National Lottery", 6, 59, 1, 59, 2, new DateOnly(2015, 10, 10),
        BonusSharesMainPool: true,
        MainPoolRules:
        [
            new PoolRule(DateOnly.MinValue, 49),
            new PoolRule(new DateOnly(2015, 10, 10), 59),
        ]);

    public static LotteryProfile EuroMillions { get; } = new(
        "euromillions", "EuroMillions", 5, 50, 2, 12, 1,
        MainPoolRules: [new PoolRule(DateOnly.MinValue, 50)],
        BonusPoolRules:
        [
            new PoolRule(DateOnly.MinValue, 9),
            new PoolRule(new DateOnly(2011, 5, 10), 11),
            new PoolRule(new DateOnly(2016, 9, 27), 12),
        ]);

    public static LotteryProfile SetForLife { get; } = new(
        "set-for-life", "Set For Life", 5, 47, 1, 10, 1,
        MainPoolRules: [new PoolRule(DateOnly.MinValue, 47)],
        BonusPoolRules: [new PoolRule(DateOnly.MinValue, 10)]);

    public static readonly IReadOnlyList<LotteryProfile> All = [UkLotto, EuroMillions, SetForLife];

    public static LotteryProfile FromKey(string? key) =>
        All.FirstOrDefault(profile => string.Equals(key, profile.Key, StringComparison.OrdinalIgnoreCase))
            ?? UkLotto;
}

public interface ILotterySelection
{
    LotteryProfile Current { get; }
}

public sealed class DefaultLotterySelection : ILotterySelection
{
    public LotteryProfile Current => LotteryProfile.UkLotto;
}
