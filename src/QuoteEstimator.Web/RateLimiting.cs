using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace QuoteEstimator.Web;

public sealed class QuoteRateLimitOptions
{
    public const string Section = "RateLimit";

    public int PermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimiting
{
    public const string QuotesPolicy = "quotes";

    /// <summary>Per-user fixed-window limit on quote generation. Rejections carry a Retry-After header.</summary>
    public static IServiceCollection AddQuoteRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<QuoteRateLimitOptions>(configuration.GetSection(QuoteRateLimitOptions.Section));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                string? detail = null;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                    detail = $"Try again in {seconds} seconds.";
                }

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many quote requests.",
                        Detail = detail,
                    },
                });
            };

            options.AddPolicy(QuotesPolicy, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<QuoteRateLimitOptions>>().Value;
                var partitionKey = httpContext.User.Identity?.Name
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }
}
