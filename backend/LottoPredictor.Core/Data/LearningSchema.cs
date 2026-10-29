using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Data;

/// <summary>EnsureCreated does nothing on an existing database, so the prospective-learning
/// tables and columns are added here. Every statement is idempotent.</summary>
public static class LearningSchema
{
    public static async Task EnsureAsync(LottoDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ShadowPredictions" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ShadowPredictions" PRIMARY KEY AUTOINCREMENT,
            "StrategyName" TEXT NOT NULL,
            "StrategyKind" TEXT NOT NULL,
            "Weights" TEXT NOT NULL,
            "CutoffSequence" INTEGER NOT NULL,
            "NumbersCsv" TEXT NOT NULL,
            "ScoresCsv" TEXT NOT NULL,
            "RandomThreePlusProbability" REAL NOT NULL,
            "RandomExpectedMatches" REAL NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "TargetDrawId" INTEGER NULL,
            "Matches" INTEGER NULL,
            "ThreePlus" INTEGER NULL,
            "ScoreZ" REAL NULL,
            "Reward" REAL NULL,
            "ScoredUtc" TEXT NULL,
            "Invalid" INTEGER NOT NULL DEFAULT 0
        );
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ShadowPredictions_StrategyName_CutoffSequence"
        ON "ShadowPredictions" ("StrategyName", "CutoffSequence");
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS "IX_ShadowPredictions_CutoffSequence"
        ON "ShadowPredictions" ("CutoffSequence");
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "HedgeWeightStates" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_HedgeWeightStates" PRIMARY KEY AUTOINCREMENT,
            "StrategyName" TEXT NOT NULL,
            "LogWeight" REAL NOT NULL,
            "UpdatedThroughSequence" INTEGER NOT NULL
        );
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_HedgeWeightStates_StrategyName"
        ON "HedgeWeightStates" ("StrategyName");
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "OnlineModelStates" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_OnlineModelStates" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "WeightsCsv" TEXT NOT NULL,
            "AccumulatorsCsv" TEXT NOT NULL,
            "Updates" INTEGER NOT NULL,
            "UpdatedThroughSequence" INTEGER NOT NULL,
            "UpdatedUtc" TEXT NOT NULL
        );
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_OnlineModelStates_Name"
        ON "OnlineModelStates" ("Name");
        """, ct);
        await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "LearningStateEntries" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_LearningStateEntries" PRIMARY KEY,
            "Value" TEXT NOT NULL
        );
        """, ct);

#pragma warning disable EF1002 // Values below are compile-time schema identifiers, never request data.
        foreach (var (column, definition) in new[]
                 {
                     ("BornAfterSequence", "INTEGER NOT NULL DEFAULT 0"),
                     ("Status", "TEXT NOT NULL DEFAULT 'Challenger'"),
                     ("RetiredUtc", "TEXT NULL"),
                 })
        {
            bool exists = (await db.Database.SqlQueryRaw<int>(
                    $"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('LearnedStrategies') WHERE name='{column}'")
                .ToListAsync(ct)).First() > 0;
            if (!exists)
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"LearnedStrategies\" ADD COLUMN \"{column}\" {definition};", ct);
        }
#pragma warning restore EF1002
    }
}
