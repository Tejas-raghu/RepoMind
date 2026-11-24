using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace RepoMind.Api.Hosting;

// Settings for an internet-facing deployment ("Demo" section). Disabled by default, so
// local development, tests and evaluations behave exactly as before.
public sealed record PublicDemoOptions(
    bool Enabled,
    bool BehindProxy,
    int AsksPerIpPerHour,
    int AsksPerDay,
    int UploadsPerIpPerDay,
    int UploadsPerDay)
{
    public static PublicDemoOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Demo");
        var options = new PublicDemoOptions(
            section.GetValue<bool>("Enabled"),
            section.GetValue<bool>("BehindProxy"),
            section.GetValue<int?>("AsksPerIpPerHour") ?? 20,
            section.GetValue<int?>("AsksPerDay") ?? 150,
            section.GetValue<int?>("UploadsPerIpPerDay") ?? 5,
            section.GetValue<int?>("UploadsPerDay") ?? 30);
        if (options.AsksPerIpPerHour < 1 || options.AsksPerDay < 1 ||
            options.UploadsPerIpPerDay < 1 || options.UploadsPerDay < 1)
            throw new InvalidOperationException("Demo limits must be positive.");
        return options;
    }
}

// Which demo budget an endpoint draws from. Attached as endpoint metadata.
public enum DemoBudget { Ask, Upload }

public sealed record DemoBudgetMetadata(DemoBudget Budget);

// Marks the Days 2-10 learning endpoints, which the public demo does not expose.
public sealed record LabEndpointMetadata;

public static class PublicDemo
{
    // Public visitors share your Cloudflare free quota and your database, so each AI or
    // upload request passes two fixed-window limits: one per client IP (fairness) and one
    // shared daily cap (protects the quota even when many IPs are used). The per-IP
    // limiter runs first, so a rejected request never consumes the shared cap.
    public static IServiceCollection AddPublicDemo(this IServiceCollection services)
    {
        // Options are read from the final configuration when the app starts, so environment
        // variables and test settings apply.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((forwarded, configuration) =>
        {
            // Render terminates HTTPS at its proxy; its address is not known in advance.
            // The default ForwardLimit of 1 reads only the entry the proxy appended.
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((limiter, configuration) =>
        {
            var options = PublicDemoOptions.From(configuration);
            var perIp = PartitionedRateLimiter.Create<HttpContext, string>(context => Budget(context) switch
            {
                DemoBudget.Ask => Window($"ask:{ClientIp(context)}", options.AsksPerIpPerHour, TimeSpan.FromHours(1)),
                DemoBudget.Upload => Window($"upload:{ClientIp(context)}", options.UploadsPerIpPerDay, TimeSpan.FromDays(1)),
                _ => RateLimitPartition.GetNoLimiter("unlimited"),
            });
            var daily = PartitionedRateLimiter.Create<HttpContext, string>(context => Budget(context) switch
            {
                DemoBudget.Ask => Window("ask:all", options.AsksPerDay, TimeSpan.FromDays(1)),
                DemoBudget.Upload => Window("upload:all", options.UploadsPerDay, TimeSpan.FromDays(1)),
                _ => RateLimitPartition.GetNoLimiter("unlimited"),
            });
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(perIp, daily);
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                var wait = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? retryAfter : TimeSpan.FromHours(1);
                var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
                rejected.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Results.Problem(
                        $"The public demo limit was reached. Try again in about {minutes} minute(s), " +
                        "or run RepoMind locally (see the README).",
                        statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(rejected.HttpContext);
            };
        });
        return services;
    }

    public static WebApplication UsePublicDemo(this WebApplication app)
    {
        var options = PublicDemoOptions.From(app.Configuration);
        if (options.Enabled && options.BehindProxy) app.UseForwardedHeaders();
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            return next(context);
        });
        if (!options.Enabled) return app;
        app.Use((context, next) => context.GetEndpoint()?.Metadata.GetMetadata<LabEndpointMetadata>() is null
            ? next(context)
            : Results.Problem("This learning endpoint is disabled in the public demo.", statusCode: 404)
                .ExecuteAsync(context));
        app.UseRateLimiter();
        return app;
    }

    public static TBuilder WithDemoBudget<TBuilder>(this TBuilder builder, DemoBudget budget)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new DemoBudgetMetadata(budget));

    public static TBuilder WithLabOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new LabEndpointMetadata());

    private static DemoBudget? Budget(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<DemoBudgetMetadata>()?.Budget;

    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Window(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}
