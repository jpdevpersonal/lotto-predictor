using LottoPredictor.Core.Data;
using LottoPredictor.Core.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "lotto.db");
builder.Services.AddDbContextFactory<LottoDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton(new AnalysisOptions());
builder.Services.AddSingleton<ICsvImporter, CsvImporter>();
builder.Services.AddSingleton<IAnalysisService, AnalysisService>();
builder.Services.AddSingleton<IDrawService, DrawService>();
builder.Services.AddSingleton<IPredictionService, PredictionService>();
builder.Services.AddSingleton<IStatisticsService, StatisticsService>();
builder.Services.AddSingleton<ILearningService, LearningService>();

builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.MapControllers();

// One-time seed: create schema and import the historical CSV if the database is empty.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LottoDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does nothing on a pre-existing database, so add the learning tables manually.
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "LearnedStrategies" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_LearnedStrategies" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "WLongTerm" REAL NOT NULL, "WRecent" REAL NOT NULL,
            "WGap" REAL NOT NULL, "WMomentum" REAL NOT NULL,
            "PairWeight" REAL NOT NULL, "PenaltyWeight" REAL NOT NULL,
            "WBias" REAL NOT NULL DEFAULT 0,
            "Generation" INTEGER NOT NULL,
            "AvgMatches" REAL NOT NULL, "RecencyWeightedAvg" REAL NOT NULL,
            "EvaluatedDraws" INTEGER NOT NULL, "CreatedUtc" TEXT NOT NULL
        );
        """);
    var wBiasExists = (await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('LearnedStrategies') WHERE name='WBias'")
        .ToListAsync()).First() > 0;
    if (!wBiasExists)
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"LearnedStrategies\" ADD COLUMN \"WBias\" REAL NOT NULL DEFAULT 0;");
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "StrategyPerformanceLogs" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_StrategyPerformanceLogs" PRIMARY KEY AUTOINCREMENT,
            "LoggedUtc" TEXT NOT NULL,
            "DrawCount" INTEGER NOT NULL,
            "StrategyName" TEXT NOT NULL,
            "AvgMatches" REAL NOT NULL, "RecencyWeightedAvg" REAL NOT NULL,
            "RandomExpected" REAL NOT NULL, "WasActive" INTEGER NOT NULL
        );
        """);
    await db.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_StrategyPerformanceLogs_DrawCount\" ON \"StrategyPerformanceLogs\" (\"DrawCount\");");

    if (!await db.Draws.AnyAsync())
    {
        var csvPath = app.Configuration["CsvImportPath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "numbers.csv");
        csvPath = Path.GetFullPath(csvPath);
        if (File.Exists(csvPath))
        {
            var importer = scope.ServiceProvider.GetRequiredService<ICsvImporter>();
            using var reader = new StreamReader(csvPath);
            var draws = importer.Parse(reader);
            db.Draws.AddRange(draws);
            await db.SaveChangesAsync();
            app.Logger.LogInformation("Imported {Count} draws from {Path}", draws.Count, csvPath);
        }
        else
        {
            app.Logger.LogWarning("CSV not found at {Path}; database is empty.", csvPath);
        }
    }
}

// Warm the analysis cache (features + walk-forward backtest) before serving requests.
try
{
    var analysis = app.Services.GetRequiredService<IAnalysisService>();
    var snapshot = await analysis.GetSnapshotAsync();
    app.Logger.LogInformation(
        "Analysis ready: {Draws} draws, pool 1-{Pool}, learning generation {Gen}, active strategy '{Strategy}'.",
        snapshot.Draws.Count, snapshot.PoolSize, snapshot.LearningGeneration, snapshot.ActiveStrategy.Name);
    app.Logger.LogInformation("Backtest verdict: {Verdict}", snapshot.Backtest.Verdict);
}
catch (InvalidOperationException ex)
{
    app.Logger.LogWarning("Analysis not available yet: {Message}", ex.Message);
}

app.Run();

public partial class Program;
