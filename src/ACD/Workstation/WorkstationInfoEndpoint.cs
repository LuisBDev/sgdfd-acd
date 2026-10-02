using ACD.Configuration;
using Microsoft.Extensions.Options;

namespace ACD.Workstation;

public static class WorkstationInfoEndpoint
{
    public static IResult Handle(HttpContext context, IOptions<AcdOptions> options)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!IsOriginAllowed(origin, options.Value.AllowedOrigins))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
        context.Response.Headers["Cache-Control"] = "no-store";

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization";
            if (context.Request.Headers["Access-Control-Request-Private-Network"]
                .ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            return Results.NoContent();
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization[7..]))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(Environment.MachineName)
            || string.IsNullOrWhiteSpace(Environment.UserName))
            return Results.Problem("Workstation information is incomplete", statusCode: StatusCodes.Status503ServiceUnavailable);

        return Results.Json(new
        {
            computerName = Environment.MachineName,
            windowsUser = Environment.UserName
        });
    }

    private static bool IsOriginAllowed(string origin, string[] allowedOrigins) =>
        !string.IsNullOrWhiteSpace(origin)
        && (allowedOrigins.Contains("*")
            || allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase));
}
