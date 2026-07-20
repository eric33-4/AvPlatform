using System.Diagnostics;
using Serilog.Context;

namespace AvPlatform.WebApi.Middleware;

/// <summary>记录接口调用摘要，并注入可跨前后端追踪的请求 ID。</summary>
public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault()
            ?? context.TraceIdentifier;
        context.Response.Headers["X-Request-Id"] = requestId;
        var stopwatch = Stopwatch.StartNew();

        using (LogContext.PushProperty("RequestId", requestId))
        using (LogContext.PushProperty("Method", context.Request.Method))
        using (LogContext.PushProperty("Path", context.Request.Path.Value))
        {
            try
            {
                await next(context);
            }
            finally
            {
                stopwatch.Stop();
                logger.LogInformation(
                    "接口调用：{Method} {Path} -> {StatusCode}，耗时 {ElapsedMs} ms，客户端 {RemoteIp}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.Elapsed.TotalMilliseconds,
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            }
        }
    }
}
