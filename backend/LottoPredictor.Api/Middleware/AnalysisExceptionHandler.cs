using LottoPredictor.Core.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LottoPredictor.Api.Middleware;

public sealed class AnalysisExceptionHandler(ILogger<AnalysisExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not AnalysisRequiredException) return false;
        logger.LogWarning(exception, "Manual analysis request cannot proceed.");
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = exception.Message,
            Instance = context.Request.Path,
        }, ct);
        return true;
    }
}
