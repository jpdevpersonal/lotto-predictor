using LottoPredictor.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace LottoPredictor.Tests;

public sealed class MutationApiKeyMiddlewareTests
{
    [Fact]
    public async Task Mutating_api_request_without_configured_key_returns_503()
    {
        var (context, nextWasCalled) = await InvokeAsync("POST", "/api/draws", configuredKey: null);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(nextWasCalled);
    }

    [Theory]
    [InlineData(null, StatusCodes.Status401Unauthorized)]
    [InlineData("wrong-key", StatusCodes.Status403Forbidden)]
    public async Task Mutating_api_request_with_missing_or_invalid_key_is_rejected(
        string? providedKey,
        int expectedStatusCode)
    {
        var (context, nextWasCalled) = await InvokeAsync(
            "POST",
            "/api/draws",
            configuredKey: "expected-key",
            providedKey: providedKey);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.False(nextWasCalled);
    }

    [Fact]
    public async Task Mutating_api_request_with_correct_key_reaches_next_delegate()
    {
        var (context, nextWasCalled) = await InvokeAsync(
            "PUT",
            "/api/draws/1",
            configuredKey: "expected-key",
            providedKey: "expected-key");

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.True(nextWasCalled);
    }

    [Theory]
    [InlineData("GET", "/api/draws/latest")]
    [InlineData("OPTIONS", "/api/draws")]
    [InlineData("POST", "/health")]
    public async Task Requests_that_do_not_require_mutation_key_reach_next_delegate(string method, string path)
    {
        var (context, nextWasCalled) = await InvokeAsync(method, path, configuredKey: null);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.True(nextWasCalled);
    }

    private static async Task<(DefaultHttpContext Context, bool NextWasCalled)> InvokeAsync(
        string method,
        string path,
        string? configuredKey,
        string? providedKey = null)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (configuredKey is not null)
            configurationValues["MutationApiKey"] = configuredKey;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (providedKey is not null)
            context.Request.Headers[MutationApiKeyMiddleware.HeaderName] = providedKey;

        var nextWasCalled = false;
        var middleware = new MutationApiKeyMiddleware(_ =>
        {
            nextWasCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, configuration);

        await middleware.InvokeAsync(context);

        return (context, nextWasCalled);
    }
}