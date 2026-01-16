using System.Text;

namespace PanoProxy;

public class BasicAuthFilter(IConfiguration config) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"PanoProxy\"";
            return Results.Unauthorized();
        }

        var encodedCredentials = authHeader["Basic ".Length..].Trim();
        string credentials;
        try
        {
            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
        }
        catch
        {
            return Results.Unauthorized();
        }

        var parts = credentials.Split(':', 2);
        if (parts.Length != 2)
        {
            return Results.Unauthorized();
        }

        var username = parts[0];
        var password = parts[1];
        var configUsername = config["BasicAuth:Username"];
        var configPassword = config["BasicAuth:Password"];

        if (username != configUsername || password != configPassword)
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}