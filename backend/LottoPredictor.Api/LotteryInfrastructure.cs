using LottoPredictor.Core.Data;
using LottoPredictor.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Api;

public sealed class HttpLotterySelection(IHttpContextAccessor httpContextAccessor) : ILotterySelection
{
    public LotteryProfile Current => LotteryProfile.FromKey(
        httpContextAccessor.HttpContext?.Request.Headers["X-Lottery"].FirstOrDefault());
}

public sealed class LotteryDbContextFactory(
    IWebHostEnvironment environment,
    ILotterySelection selection) : IDbContextFactory<LottoDbContext>
{
    public LottoDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LottoDbContext>()
            .UseSqlite($"Data Source={DatabasePath(environment.ContentRootPath, selection.Current)}")
            .Options;
        return new LottoDbContext(options);
    }

    public Task<LottoDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public static string DatabasePath(string contentRootPath, LotteryProfile profile) =>
        Path.Combine(contentRootPath, profile == LotteryProfile.EuroMillions
            ? "euromillions.db"
            : "lotto.db");
}
