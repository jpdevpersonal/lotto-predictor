using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LottoPredictor.Api.Controllers;

[ApiController]
public class LearningController(ILearningService learning) : ControllerBase
{
    [HttpGet("api/learning")]
    public async Task<ActionResult<LearningDto>> GetLearning(
        [FromQuery] int historyLimit = 500, CancellationToken ct = default)
        => Ok(await learning.GetLearningAsync(historyLimit, ct));
}
