using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LottoPredictor.Api.Controllers;

[ApiController]
public class StatisticsController(IStatisticsService statistics) : ControllerBase
{
    [HttpGet("api/statistics")]
    public async Task<ActionResult<StatisticsDto>> GetStatistics(CancellationToken ct = default)
        => Ok(await statistics.GetStatisticsAsync(ct));

    [HttpGet("api/backtesting")]
    public async Task<ActionResult<BacktestingDto>> GetBacktesting(CancellationToken ct = default)
        => Ok(await statistics.GetBacktestingAsync(ct));
}
