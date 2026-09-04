using System.Globalization;
using LottoPredictor.Core.Models;

namespace LottoPredictor.Core.Services;

public interface ICsvImporter
{
    /// <summary>Parses the historical CSV and returns draws in chronological order with
    /// Sequence assigned (oldest = 1).</summary>
    List<Draw> Parse(TextReader reader);
}

public interface IEuroMillionsCsvImporter
{
    List<Draw> Parse(TextReader reader);
}

/// <summary>Parses the supplied numbers.csv.
///
/// Notes discovered by inspecting the file:
/// - Headers and values carry leading/trailing padding spaces; everything is trimmed.
/// - Rows are ordered newest-first.
/// - Draw numbers 3179+ appear TWICE with different number sets, machines and ball sets:
///   from that point the operator ran two independent machine draws per event. Both rows are
///   genuine samples from the same ball pool, so BOTH are kept as separate draw events.
///   Nothing is discarded. Chronological order within a shared draw number preserves the
///   file's own ordering (the row printed lower in the newest-first file is treated as earlier).
/// </summary>
public class CsvImporter : ICsvImporter
{
    public List<Draw> Parse(TextReader reader)
    {
        string? headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("CSV file is empty.");
        var headers = headerLine.Split(',').Select(h => h.Trim().TrimEnd('.')).ToArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++) index[headers[i]] = i;

        foreach (var required in new[] { "No", "DD", "MMM", "YYYY", "N1", "N2", "N3", "N4", "N5", "N6" })
            if (!index.ContainsKey(required))
                throw new InvalidDataException($"CSV is missing required column '{required}'.");

        var rows = new List<(Draw Draw, int FileIndex)>();
        string? line;
        int fileIndex = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',');
            if (fields.Length < headers.Length)
                throw new InvalidDataException($"CSV row {fileIndex + 2} has {fields.Length} fields, expected {headers.Length}.");

            string Get(string col) => fields[index[col]].Trim();
            int GetInt(string col) => int.Parse(Get(col), CultureInfo.InvariantCulture);

            var date = DateOnly.ParseExact(
                $"{GetInt("DD")} {Get("MMM")} {GetInt("YYYY")}", "d MMM yyyy", CultureInfo.InvariantCulture);

            var numbers = new[] { GetInt("N1"), GetInt("N2"), GetInt("N3"), GetInt("N4"), GetInt("N5"), GetInt("N6") };
            if (numbers.Distinct().Count() != 6)
                throw new InvalidDataException($"CSV row {fileIndex + 2} contains duplicate numbers.");

            var draw = new Draw
            {
                DrawNumber = GetInt("No"),
                Date = date,
                Bonus = index.ContainsKey("BN") ? GetInt("BN") : null,
                Jackpot = index.ContainsKey("Jackpot") ? long.Parse(Get("Jackpot"), CultureInfo.InvariantCulture) : 0,
                Wins = index.ContainsKey("Wins") ? GetInt("Wins") : 0,
                Machine = index.TryGetValue("Machine", out var mi) ? fields[mi].Trim() : "",
                BallSet = index.TryGetValue("Set", out var si) ? fields[si].Trim() : "",
                Source = "csv",
            };
            draw.SetNumbers(numbers);
            rows.Add((draw, fileIndex));
            fileIndex++;
        }

        // Oldest first; within a duplicated draw number, reverse of file order (file is newest-first).
        var ordered = rows
            .OrderBy(r => r.Draw.DrawNumber)
            .ThenByDescending(r => r.FileIndex)
            .Select(r => r.Draw)
            .ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Sequence = i + 1;
        return ordered;
    }
}

public class EuroMillionsCsvImporter : IEuroMillionsCsvImporter
{
    public List<Draw> Parse(TextReader reader)
    {
        string? headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("CSV file is empty.");
        var headers = headerLine.Split(',').Select(header => header.Trim()).ToArray();
        var index = headers
            .Select((header, position) => (header, position))
            .ToDictionary(item => item.header, item => item.position, StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[]
                 {
                     "draw_date", "draw_id", "N1", "N2", "N3", "N4", "N5",
                     "lucky_star_1", "lucky_star_2",
                 })
        {
            if (!index.ContainsKey(required))
                throw new InvalidDataException($"CSV is missing required column '{required}'.");
        }

        var draws = new List<Draw>();
        string? line;
        int rowNumber = 1;
        while ((line = reader.ReadLine()) != null)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = line.Split(',');
            if (fields.Length < headers.Length)
                throw new InvalidDataException($"CSV row {rowNumber} has {fields.Length} fields, expected {headers.Length}.");

            string Get(string column) => fields[index[column]].Trim();
            int GetInt(string column) => int.Parse(Get(column), CultureInfo.InvariantCulture);

            var numbers = new[] { GetInt("N1"), GetInt("N2"), GetInt("N3"), GetInt("N4"), GetInt("N5") };
            var stars = new[] { GetInt("lucky_star_1"), GetInt("lucky_star_2") };
            if (numbers.Distinct().Count() != numbers.Length)
                throw new InvalidDataException($"CSV row {rowNumber} contains duplicate main numbers.");
            if (stars.Distinct().Count() != stars.Length)
                throw new InvalidDataException($"CSV row {rowNumber} contains duplicate Lucky Stars.");

            var draw = new Draw
            {
                DrawNumber = GetInt("draw_id"),
                Date = DateOnly.ParseExact(Get("draw_date"), "dd/MM/yyyy", CultureInfo.InvariantCulture),
                Bonus = stars.Min(),
                Bonus2 = stars.Max(),
                Machine = "EuroMillions",
                Source = "csv",
            };
            draw.SetNumbers(numbers);
            draws.Add(draw);
        }

        var ordered = draws.OrderBy(draw => draw.Date).ThenBy(draw => draw.DrawNumber).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Sequence = i + 1;
        return ordered;
    }
}
