using System.Diagnostics;
using Atlas.Api.Services;

namespace Atlas.Api.Middleware;

public sealed class RequestObservabilityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestObservabilityMiddleware> _logger;

    public RequestObservabilityMiddleware(RequestDelegate next, ILogger<RequestObservabilityMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ISystemMonitoringService monitoring)
    {
        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "unknown";
        string method = context.Request.Method;
        var sw = Stopwatch.StartNew();

        using var activity = SystemMonitoringService.ActivitySource.StartActivity("http.request", ActivityKind.Server);
        activity?.SetTag("http.method", method);
        activity?.SetTag("http.route", path);

        context.Response.Headers["X-Trace-Id"] = activity?.TraceId.ToString() ?? context.TraceIdentifier;

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            activity?.SetTag("http.aborted", true);
            _logger.LogDebug("Request aborted by client {Method} {Path}", method, path);
        }
        catch (Exception ex)
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            monitoring.PublishAlert("api", Models.NotificationSeverity.Critical, $"Unhandled exception on {method} {path}: {ex.GetType().Name}");
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}", method, path);
            throw;
        }
        finally
        {
            sw.Stop();
            int statusCode = context.Response.StatusCode;
            monitoring.ObserveRequest(path, statusCode, sw.Elapsed.TotalMilliseconds);
            activity?.SetTag("http.status_code", statusCode);
            activity?.SetTag("http.duration_ms", sw.Elapsed.TotalMilliseconds);

            _logger.LogInformation(
                "HTTP {Method} {Path} -> {StatusCode} ({DurationMs} ms)",
                method,
                path,
                statusCode,
                sw.Elapsed.TotalMilliseconds.ToString("F1"));
        }
    }
}
