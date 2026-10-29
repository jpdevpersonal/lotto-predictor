using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Data;

public class LottoDbContext(DbContextOptions<LottoDbContext> options) : DbContext(options)
{
    public DbSet<Draw> Draws => Set<Draw>();
    public DbSet<Prediction> Predictions => Set<Prediction>();
    public DbSet<PredictionEvaluation> PredictionEvaluations => Set<PredictionEvaluation>();
    public DbSet<LearnedStrategy> LearnedStrategies => Set<LearnedStrategy>();
    public DbSet<StrategyPerformanceLog> StrategyPerformanceLogs => Set<StrategyPerformanceLog>();
    public DbSet<ShadowPrediction> ShadowPredictions => Set<ShadowPrediction>();
    public DbSet<HedgeWeightState> HedgeWeightStates => Set<HedgeWeightState>();
    public DbSet<OnlineModelState> OnlineModelStates => Set<OnlineModelState>();
    public DbSet<LearningStateEntry> LearningStateEntries => Set<LearningStateEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Draw>().HasIndex(d => d.Sequence).IsUnique();
        modelBuilder.Entity<Draw>().HasIndex(d => d.DrawNumber);
        modelBuilder.Entity<Prediction>().HasIndex(p => p.CutoffSequence);
        modelBuilder.Entity<PredictionEvaluation>()
            .HasIndex(evaluation => new { evaluation.PredictionId, evaluation.EvaluatedDrawId })
            .IsUnique();
        modelBuilder.Entity<PredictionEvaluation>()
            .HasOne(evaluation => evaluation.Prediction)
            .WithMany(prediction => prediction.Evaluations)
            .HasForeignKey(evaluation => evaluation.PredictionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PredictionEvaluation>()
            .HasOne(evaluation => evaluation.EvaluatedDraw)
            .WithMany()
            .HasForeignKey(evaluation => evaluation.EvaluatedDrawId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StrategyPerformanceLog>().HasIndex(l => l.DrawCount);
        modelBuilder.Entity<ShadowPrediction>()
            .HasIndex(s => new { s.StrategyName, s.CutoffSequence }).IsUnique();
        modelBuilder.Entity<ShadowPrediction>().HasIndex(s => s.CutoffSequence);
        modelBuilder.Entity<HedgeWeightState>().HasIndex(h => h.StrategyName).IsUnique();
        modelBuilder.Entity<OnlineModelState>().HasIndex(m => m.Name).IsUnique();
        modelBuilder.Entity<LearningStateEntry>().HasKey(e => e.Key);
    }
}
