using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Data;

public class LottoDbContext(DbContextOptions<LottoDbContext> options) : DbContext(options)
{
    public DbSet<Draw> Draws => Set<Draw>();
    public DbSet<Prediction> Predictions => Set<Prediction>();
    public DbSet<LearnedStrategy> LearnedStrategies => Set<LearnedStrategy>();
    public DbSet<StrategyPerformanceLog> StrategyPerformanceLogs => Set<StrategyPerformanceLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Draw>().HasIndex(d => d.Sequence).IsUnique();
        modelBuilder.Entity<Draw>().HasIndex(d => d.DrawNumber);
        modelBuilder.Entity<Prediction>().HasIndex(p => p.CutoffSequence);
        modelBuilder.Entity<StrategyPerformanceLog>().HasIndex(l => l.DrawCount);
    }
}
