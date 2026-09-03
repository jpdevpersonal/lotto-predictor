using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LottoPredictor.Api.Controllers;

[ApiController]
[Route("api/draws")]
public class DrawsController(IDrawService draws) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DrawDto>>> GetDraws([FromQuery] int limit = 50, CancellationToken ct = default)
        => Ok(await draws.GetDrawsAsync(limit, ct));

    [HttpGet("history")]
    public async Task<ActionResult<DrawHistoryDto>> GetHistory(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        [FromQuery] bool loadAll = false,
        CancellationToken ct = default)
        => Ok(await draws.GetDrawHistoryAsync(offset, limit, loadAll, ct));

    [HttpGet("latest")]
    public async Task<ActionResult<DrawDto>> GetLatest(CancellationToken ct = default)
    {
        var latest = await draws.GetLatestAsync(ct);
        return latest is null ? NotFound() : Ok(latest);
    }

    [HttpGet("count")]
    public async Task<ActionResult<int>> GetCount(CancellationToken ct = default)
        => Ok(await draws.CountAsync(ct));

    [HttpPost]
    public async Task<ActionResult<DrawDto>> AddDraw([FromBody] AddDrawRequest request, CancellationToken ct = default)
    {
        try
        {
            return Ok(await draws.AddDrawAsync(request, ct));
        }
        catch (ValidationFailedException ex)
        {
            return BadRequest(new { errors = ex.Errors });
        }
    }

    [HttpPost("rounds")]
    public async Task<ActionResult<IReadOnlyList<DrawDto>>> AddDrawRounds(
        [FromBody] AddDrawRoundsRequest request, CancellationToken ct = default)
    {
        try
        {
            return Ok(await draws.AddDrawRoundsAsync(request, ct));
        }
        catch (ValidationFailedException ex)
        {
            return BadRequest(new { errors = ex.Errors });
        }
    }

    [HttpPost("latest/round")]
    public async Task<ActionResult<DrawDto>> AddLatestRound(
        [FromBody] AddDrawRequest request, CancellationToken ct = default)
    {
        try
        {
            return Ok(await draws.AddLatestRoundAsync(request, ct));
        }
        catch (ValidationFailedException ex)
        {
            return BadRequest(new { errors = ex.Errors });
        }
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<DrawDto>> UpdateDraw(
        int id, [FromBody] UpdateDrawRequest request, CancellationToken ct = default)
    {
        try
        {
            return Ok(await draws.UpdateDrawAsync(id, request, ct));
        }
        catch (ValidationFailedException ex)
        {
            return BadRequest(new { errors = ex.Errors });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
