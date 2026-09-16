using System.Security.Cryptography;
using System.Text;

namespace LottoPredictor.Api;

public sealed class MutationApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public const string HeaderName = "X-Api-Key";
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresApiKey(context.Request))
        {
            await next(context);
            return;
        }

        var expectedKey = configuration["MutationApiKey"];
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var providedValues) || providedValues.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!ApiKeysEqual(expectedKey, providedValues[0]))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool RequiresApiKey(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && MutatingMethods.Contains(request.Method);

    private static bool ApiKeysEqual(string expectedKey, string? providedKey)
    {
        if (providedKey is null)
            return false;

        var expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        var providedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

public static class MutationApiKeyMiddlewareExtensions
{
    public static IApplicationBuilder UseMutationApiKey(this IApplicationBuilder app) =>
        app.UseMiddleware<MutationApiKeyMiddleware>();
}