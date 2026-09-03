using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LottoPredictor.Api.Controllers;

[ApiController]
[Route("api/predictions")]
public class PredictionsController(IPredictionService predictions) : ControllerBase
{
    [HttpGet("latest")]
    public async Task<ActionResult<PredictionDto>> GetLatest(CancellationToken ct = default)
    {
        var latest = await predictions.GetLatestAsync(ct);
        return latest is null ? NotFound() : Ok(latest);
    }

    [HttpPost("generate")]
    public async Task<ActionResult<PredictionDto>> Generate(CancellationToken ct = default)
        => Ok(await predictions.GenerateAsync(ct));

    [HttpGet("lines")]
    public async Task<ActionResult<PredictionLinesDto>> GetLines(
        [FromQuery] int count = 50, CancellationToken ct = default)
        => Ok(await predictions.GenerateLinesAsync(count, ct));

    [HttpGet("lines/best")]
    public async Task<ActionResult<BestOfLinesDto>> GetBestOfLines(
        [FromQuery] int count = 50, CancellationToken ct = default)
        => Ok(await predictions.GenerateBestOfLinesAsync(count, ct));

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<PredictionDto>>> GetHistory([FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await predictions.GetHistoryAsync(limit, ct));
}
