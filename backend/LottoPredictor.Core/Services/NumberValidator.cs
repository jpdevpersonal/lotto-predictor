namespace LottoPredictor.Core.Services;

public static class NumberValidator
{
    /// <summary>Validates a manually entered set of six numbers against the pool inferred
    /// from the historical data. Returns an empty list when valid.</summary>
    public static List<string> Validate(IReadOnlyList<int>? numbers, int poolSize)
    {
        var errors = new List<string>();
        if (numbers is null || numbers.Count != 6)
        {
            errors.Add("Exactly six numbers are required.");
            return errors;
        }
        if (numbers.Distinct().Count() != 6)
            errors.Add("Numbers must not contain duplicates.");
        foreach (var n in numbers.Where(n => n < 1 || n > poolSize).Distinct())
            errors.Add($"Number {n} is outside the permitted range 1-{poolSize}.");
        return errors;
    }
}
