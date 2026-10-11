using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LottoPredictor.Api.Controllers;

[ApiController]
[Route("api/analysis")]
public sealed class AnalysisController(IAnalysisService analysis) : ControllerBase
{
    [HttpGet("status")]
    [EndpointSummary("Read manual recalculation status for the selected lottery.")]
    public async Task<ActionResult<AnalysisStatusDto>> GetStatus(CancellationToken ct)
        => Ok(await analysis.GetStatusAsync(ct));

    [HttpPost("recalculate")]
    [EndpointSummary("Recalculate the selected lottery using recent or full-history validation.")]
    [ProducesResponseType<AnalysisStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AnalysisStatusDto>> Recalculate(
        [FromBody] RecalculateAnalysisRequest request, CancellationToken ct)
    {
        await analysis.RecalculateAsync(request.UseAllHistory, ct);
        return Ok(await analysis.GetStatusAsync(ct));
    }
}
