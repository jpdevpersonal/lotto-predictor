using LottoPredictor.Api;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ILotterySelection, HttpLotterySelection>();
builder.Services.AddSingleton<IDbContextFactory<LottoDbContext>, LotteryDbContextFactory>();

builder.Services.AddSingleton(new AnalysisOptions());
builder.Services.AddSingleton<ICsvImporter, CsvImporter>();
builder.Services.AddSingleton<IEuroMillionsCsvImporter, EuroMillionsCsvImporter>();
builder.Services.AddSingleton<ISetForLifeCsvImporter, SetForLifeCsvImporter>();
builder.Services.AddSingleton<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<IDrawService, DrawService>();
builder.Services.AddScoped<IPredictionService, PredictionService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddScoped<ILearningService, LearningService>();

builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.MapControllers();

// One-time seed: create schema and import the historical CSV if the database is empty.
using (var scope = app.Services.CreateScope())
{
    foreach (var lottery in LotteryProfile.All)
    {
        var options = new DbContextOptionsBuilder<LottoDbContext>()
            .UseSqlite($"Data Source={LotteryDbContextFactory.DatabasePath(builder.Environment.ContentRootPath, lottery)}")
            .Options;
        await using var db = new LottoDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // EnsureCreated does nothing on a pre-existing database, so add newer tables manually.
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "LearnedStrategies" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_LearnedStrategies" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "WLongTerm" REAL NOT NULL, "WRecent" REAL NOT NULL,
            "WGap" REAL NOT NULL, "WMomentum" REAL NOT NULL,
            "PairWeight" REAL NOT NULL, "PenaltyWeight" REAL NOT NULL,
            "WBias" REAL NOT NULL DEFAULT 0,
            "WBonus" REAL NOT NULL DEFAULT 0,
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
        var wBonusExists = (await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('LearnedStrategies') WHERE name='WBonus'")
            .ToListAsync()).First() > 0;
        if (!wBonusExists)
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"LearnedStrategies\" ADD COLUMN \"WBonus\" REAL NOT NULL DEFAULT 0;");
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
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "PredictionEvaluations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PredictionEvaluations" PRIMARY KEY AUTOINCREMENT,
            "PredictionId" INTEGER NOT NULL,
            "EvaluatedDrawId" INTEGER NOT NULL,
            "ActualNumbersCsv" TEXT NOT NULL,
            "Matches" INTEGER NOT NULL,
            "BonusMatches" INTEGER NULL,
            "ActualLuckyStarsCsv" TEXT NULL,
            "LuckyStarMatches" INTEGER NULL,
            "EvaluatedUtc" TEXT NOT NULL,
            CONSTRAINT "FK_PredictionEvaluations_Predictions_PredictionId"
                FOREIGN KEY ("PredictionId") REFERENCES "Predictions" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_PredictionEvaluations_Draws_EvaluatedDrawId"
                FOREIGN KEY ("EvaluatedDrawId") REFERENCES "Draws" ("Id") ON DELETE CASCADE
        );
        """);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PredictionEvaluations_PredictionId_EvaluatedDrawId"
        ON "PredictionEvaluations" ("PredictionId", "EvaluatedDrawId");
        """);

    #pragma warning disable EF1002 // Values below are compile-time schema identifiers, never request data.
        foreach (var (table, column, definition) in new[]
                 {
                     ("Draws", "Bonus2", "INTEGER NULL"),
                     ("Predictions", "LuckyStarsCsv", "TEXT NULL"),
                     ("Predictions", "ActualLuckyStarsCsv", "TEXT NULL"),
                     ("Predictions", "LuckyStarMatches", "INTEGER NULL"),
                 })
        {
            bool exists = (await db.Database.SqlQueryRaw<int>(
                    $"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('{table}') WHERE name='{column}'")
                .ToListAsync()).First() > 0;
            if (!exists)
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
        }
#pragma warning restore EF1002

        var bonusMatchesSql = lottery.BonusSharesMainPool
            ? "CASE WHEN d.\"Bonus\" IN (p.\"P1\", p.\"P2\", p.\"P3\", p.\"P4\", p.\"P5\", p.\"P6\") THEN 1 ELSE 0 END"
            : "NULL";
#pragma warning disable EF1002 // The only interpolation is the fixed expression selected above.
        await db.Database.ExecuteSqlRawAsync($$"""
        INSERT OR IGNORE INTO "PredictionEvaluations" (
            "PredictionId", "EvaluatedDrawId", "ActualNumbersCsv", "Matches",
            "BonusMatches", "ActualLuckyStarsCsv", "LuckyStarMatches", "EvaluatedUtc")
        SELECT p."Id", p."EvaluatedDrawId", COALESCE(p."ActualNumbersCsv", ''), p."Matches",
            {{bonusMatchesSql}}, p."ActualLuckyStarsCsv", p."LuckyStarMatches",
            COALESCE(p."EvaluatedUtc", p."CreatedUtc")
        FROM "Predictions" p
        JOIN "Draws" d ON d."Id" = p."EvaluatedDrawId"
        WHERE p."Matches" IS NOT NULL AND p."EvaluatedDrawId" IS NOT NULL;
        """);
#pragma warning restore EF1002

        if (!await db.Draws.AnyAsync())
        {
            string csvPath = lottery == LotteryProfile.EuroMillions
                ? app.Configuration["EuroMillionsCsvImportPath"]
                    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "euromillions.csv")
                : lottery == LotteryProfile.SetForLife
                    ? app.Configuration["SetForLifeCsvImportPath"]
                        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "set_for_life.csv")
                    : app.Configuration["CsvImportPath"]
                        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "numbers.csv");
            csvPath = Path.GetFullPath(csvPath);
            if (File.Exists(csvPath))
            {
                using var reader = new StreamReader(csvPath);
                var draws = lottery == LotteryProfile.EuroMillions
                    ? scope.ServiceProvider.GetRequiredService<IEuroMillionsCsvImporter>().Parse(reader)
                    : lottery == LotteryProfile.SetForLife
                        ? scope.ServiceProvider.GetRequiredService<ISetForLifeCsvImporter>().Parse(reader)
                        : scope.ServiceProvider.GetRequiredService<ICsvImporter>().Parse(reader);
                db.Draws.AddRange(draws);
                await db.SaveChangesAsync();
                app.Logger.LogInformation(
                    "Imported {Count} {Lottery} draws from {Path}", draws.Count, lottery.Name, csvPath);
            }
            else
            {
                app.Logger.LogWarning(
                    "{Lottery} CSV not found at {Path}; database is empty.", lottery.Name, csvPath);
            }
        }
    }
}

// Warm the analysis cache after Kestrel starts so readiness is not blocked by the backtest.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
        var snapshot = await analysis.GetSnapshotAsync(app.Lifetime.ApplicationStopping);
        app.Logger.LogInformation(
            "Analysis ready: {Draws} draws, pool 1-{Pool}, learning generation {Gen}, active strategy '{Strategy}'.",
            snapshot.Draws.Count, snapshot.PoolSize, snapshot.LearningGeneration, snapshot.ActiveStrategy.Name);
        app.Logger.LogInformation("Backtest verdict: {Verdict}", snapshot.Backtest.Verdict);
    }
    catch (InvalidOperationException ex)
    {
        app.Logger.LogWarning("Analysis not available yet: {Message}", ex.Message);
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        app.Logger.LogInformation("Analysis warm-up canceled because the application is stopping.");
    }
}));

app.Run();

public partial class Program;
