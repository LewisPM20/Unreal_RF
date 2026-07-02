using Microsoft.Extensions.Options;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Optional bearer-token protection for controller mutation endpoints.
/// </summary>
public sealed class ControllerSecurityOptions
{
    public string? ApiToken { get; set; }
}

public static class ApiTokenProtection
{
    private static readonly string[] ProtectedPrefixes =
    [
        "/api/workers",
        "/api/jobs",
        "/api/projects",
        "/api/render-profiles",
        "/api/settings",
        "/api/queue",
        "/api/admin"
    ];

    public static IApplicationBuilder UseOptionalApiTokenProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<ControllerSecurityOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ApiToken) || !RequiresToken(context.Request.Method, context.Request.Path))
            {
                await next(context);
                return;
            }

            if (IsAuthorized(context.Request.Headers.Authorization.ToString(), options.ApiToken))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid controller API token is required." });
        });

    public static bool RequiresToken(string method, PathString path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return false;
        }

        var value = path.Value ?? string.Empty;
        return ProtectedPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAuthorized(string? authorizationHeader, string configuredToken)
    {
        const string bearerPrefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader) || string.IsNullOrWhiteSpace(configuredToken))
        {
            return false;
        }

        return authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(authorizationHeader[bearerPrefix.Length..].Trim(), configuredToken, StringComparison.Ordinal);
    }
}

public static class SecretRedaction
{
    public static string DescribeConfiguredSecret(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "not configured" : "configured (redacted)";
}
