using LottoPredictor.Core.Services;

namespace LottoPredictor.Tests;

public class CsvImporterTests
{
    private const string Header =
        "No., Day,DD,MMM,YYYY, N1,N2,N3,N4,N5,N6,BN,   Jackpot, Wins,   Machine  ,Set";

    private static List<LottoPredictor.Core.Models.Draw> Parse(params string[] lines)
    {
        var importer = new CsvImporter();
        using var reader = new StringReader(string.Join("\n", lines));
        return importer.Parse(reader);
    }

    [Fact]
    public void Parses_row_with_padded_headers_and_values()
    {
        var draws = Parse(Header,
            "3202, Sat,29,Aug,2026, 02,06,28,33,43,54,55,   4349673,    0,   Lotto 4  ,  9 ");

        var d = Assert.Single(draws);
        Assert.Equal(3202, d.DrawNumber);
        Assert.Equal(new DateOnly(2026, 8, 29), d.Date);
        Assert.Equal(new[] { 2, 6, 28, 33, 43, 54 }, d.Numbers());
        Assert.Equal(55, d.Bonus);
        Assert.Equal(4349673, d.Jackpot);
        Assert.Equal("Lotto 4", d.Machine);
        Assert.Equal("9", d.BallSet);
        Assert.Equal(1, d.Sequence);
    }

    [Fact]
    public void Keeps_both_rows_of_a_duplicated_draw_number_as_separate_events()
    {
        var draws = Parse(Header,
            "3202, Sat,29,Aug,2026, 02,06,28,33,43,54,55,   4349673,    0,   Lotto 4  ,  9 ",
            "3202, Sat,29,Aug,2026, 02,24,41,43,45,59,54,   4349673,    0,   Lotto 5  , 10 ",
            "3201, Wed,26,Aug,2026, 11,13,14,21,47,54,52,   2865432,    0,   Lotto 4  ,  9 ");

        Assert.Equal(3, draws.Count);
        // Oldest first, and both 3202 events retained.
        Assert.Equal([3201, 3202, 3202], draws.Select(d => d.DrawNumber).ToArray());
        Assert.Equal([1, 2, 3], draws.Select(d => d.Sequence).ToArray());
        Assert.NotEqual(draws[1].Numbers(), draws[2].Numbers());
    }

    [Fact]
    public void Orders_newest_first_file_into_chronological_sequence()
    {
        var draws = Parse(Header,
            "3, Sat, 3,Dec,1994, 11,17,21,29,30,40,31,   6906572,    0,   Arthur   ,  B ",
            "2, Sat,26,Nov,1994, 06,12,15,16,31,44,37,   1760966,    4,   Arthur   ,  B ",
            "1, Sat,19,Nov,1994, 03,05,14,22,30,44,10,    839254,    7,  Guinevere ,  A ");

        Assert.Equal([1, 2, 3], draws.Select(d => d.DrawNumber).ToArray());
        Assert.Equal([1, 2, 3], draws.Select(d => d.Sequence).ToArray());
    }

    [Fact]
    public void Rejects_row_with_duplicate_numbers()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Header,
            "1, Sat,19,Nov,1994, 03,03,14,22,30,44,10,    839254,    7,  Guinevere ,  A "));
    }

    [Fact]
    public void Rejects_missing_required_column()
    {
        Assert.Throws<InvalidDataException>(() => Parse(
            "No., Day,DD,MMM,YYYY, N1,N2,N3,N4,N5,BN",
            "1, Sat,19,Nov,1994, 03,05,14,22,30,44"));
    }

    [Fact]
    public void Stores_numbers_sorted_ascending()
    {
        var draws = Parse(Header,
            "1, Sat,19,Nov,1994, 44,05,14,22,30,03,10,    839254,    7,  Guinevere ,  A ");
        Assert.Equal(new[] { 3, 5, 14, 22, 30, 44 }, draws[0].Numbers());
    }
}

public class EuroMillionsCsvImporterTests
{
    [Fact]
    public void Parses_attached_format_with_five_numbers_and_two_lucky_stars()
    {
        const string csv = """
            draw_date,draw_id,N1,N2,N3,N4,N5,lucky_star_1,lucky_star_2
            20/02/2004,22004,7,13,39,47,50,2,5
            13/02/2004,12004,16,29,32,36,41,7,9
            """;
        var importer = new EuroMillionsCsvImporter();
        using var reader = new StringReader(csv);

        var draws = importer.Parse(reader);

        Assert.Equal(2, draws.Count);
        Assert.Equal([1, 2], draws.Select(draw => draw.Sequence));
        Assert.Equal(new DateOnly(2004, 2, 13), draws[0].Date);
        Assert.Equal([16, 29, 32, 36, 41], draws[0].Numbers());
        Assert.Equal([7, 9], draws[0].BonusNumbers());
        Assert.Null(draws[0].N6);
    }
}
