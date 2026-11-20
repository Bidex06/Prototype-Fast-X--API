using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TradingBotEngine.API.Infrastructure;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        _logger.LogError(
            exception,
            "Unhandled exception. TraceId: {TraceId}",
            traceId);

        var statusCode = exception switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            InvalidOperationException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status500InternalServerError
        };

        var code = statusCode switch
        {
            StatusCodes.Status400BadRequest => "BAD_REQUEST",
            StatusCodes.Status401Unauthorized => "UNAUTHORIZED",
            StatusCodes.Status404NotFound => "NOT_FOUND",
            _ => "INTERNAL_SERVER_ERROR"
        };

        var message = statusCode switch
        {
            StatusCodes.Status400BadRequest =>
                "The request could not be processed.",
            StatusCodes.Status401Unauthorized =>
                "You are not authorized to perform this action.",
            StatusCodes.Status404NotFound =>
                "The requested resource was not found.",
            _ =>
                "An unexpected error occurred while processing the request."
        };

        httpContext.Response.StatusCode = statusCode;

        // The exception handler resets the response, which can drop the CORS headers and make the
        // browser show a network error instead of the real one. Re-apply the app's CORS policy.
        try
        {
            var corsPolicyProvider = httpContext.RequestServices.GetRequiredService<ICorsPolicyProvider>();
            var corsService = httpContext.RequestServices.GetRequiredService<ICorsService>();
            var corsPolicy = await corsPolicyProvider.GetPolicyAsync(httpContext, "AllowReactApp");
            if (corsPolicy != null)
            {
                var corsResult = corsService.EvaluatePolicy(httpContext, corsPolicy);
                corsService.ApplyResult(corsResult, httpContext.Response);
            }
        }
        catch (Exception corsException)
        {
            _logger.LogWarning(corsException, "Could not re-apply the CORS policy to an error response.");
        }

        var response = new ProblemDetails
        {
            Status = statusCode,
            Title = code,
            Detail = message,
            Instance = httpContext.Request.Path
        };

        response.Extensions["code"] = code;
        response.Extensions["message"] = message;
        response.Extensions["traceId"] = traceId;

        httpContext.Response.ContentType =
            "application/problem+json";

        await httpContext.Response.WriteAsJsonAsync(
            response,
            cancellationToken);

        return true;
    }
}