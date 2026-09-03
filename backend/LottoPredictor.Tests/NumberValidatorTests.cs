using LottoPredictor.Core.Services;

namespace LottoPredictor.Tests;

public class NumberValidatorTests
{
    [Fact]
    public void Accepts_valid_set() =>
        Assert.Empty(NumberValidator.Validate([1, 2, 3, 4, 5, 59], 59));

    [Fact]
    public void Rejects_null() =>
        Assert.NotEmpty(NumberValidator.Validate(null, 59));

    [Theory]
    [InlineData(new int[] { 1, 2, 3, 4, 5 })]
    [InlineData(new int[] { 1, 2, 3, 4, 5, 6, 7 })]
    public void Rejects_wrong_count(int[] numbers) =>
        Assert.NotEmpty(NumberValidator.Validate(numbers, 59));

    [Fact]
    public void Rejects_duplicates() =>
        Assert.Contains(NumberValidator.Validate([1, 1, 3, 4, 5, 6], 59),
            e => e.Contains("duplicates"));

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(-5)]
    public void Rejects_out_of_range(int bad) =>
        Assert.Contains(NumberValidator.Validate([1, 2, 3, 4, 5, bad], 59),
            e => e.Contains("outside the permitted range"));
}
