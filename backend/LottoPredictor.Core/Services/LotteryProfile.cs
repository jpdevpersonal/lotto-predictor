namespace LottoPredictor.Core.Services;

public sealed record LotteryProfile(
    string Key,
    string Name,
    int MainNumberCount,
    int MainPoolSize,
    int BonusNumberCount,
    int BonusPoolSize,
    int RoundCount)
{
    public static LotteryProfile UkLotto { get; } = new(
        "uk-lotto", "UK National Lottery", 6, 59, 1, 59, 2);

    public static LotteryProfile EuroMillions { get; } = new(
        "euromillions", "EuroMillions", 5, 50, 2, 12, 1);

    public static LotteryProfile FromKey(string? key) =>
        string.Equals(key, EuroMillions.Key, StringComparison.OrdinalIgnoreCase)
            ? EuroMillions
            : UkLotto;
}

public interface ILotterySelection
{
    LotteryProfile Current { get; }
}

public sealed class DefaultLotterySelection : ILotterySelection
{
    public LotteryProfile Current => LotteryProfile.UkLotto;
}
