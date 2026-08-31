using System.Security.Cryptography;

namespace MyloMail.Api.Security;

/// <summary>Rejects local callers that do not possess the token Electron generated for this launch (§9).</summary>
public sealed class LaunchTokenMiddleware(RequestDelegate next, string expectedToken)
{
	public async Task InvokeAsync(HttpContext context)
	{
		var supplied = context.Request.Headers.Authorization.ToString();
		const string scheme = "Bearer ";
		if (!supplied.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

		var candidate = supplied[scheme.Length..];
		var valid = CryptographicOperations.FixedTimeEquals(
			System.Text.Encoding.UTF8.GetBytes(expectedToken),
			System.Text.Encoding.UTF8.GetBytes(candidate)
		);
		if (!valid)
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

		await next(context);
	}
}

public static class LaunchTokenApplicationBuilderExtensions
{
	public const string EnvironmentVariable = "MYLOMAIL_LAUNCH_TOKEN";

	public static IApplicationBuilder UseLaunchToken(this IApplicationBuilder app)
	{
		var token = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(token))
		{
			throw new InvalidOperationException($"{EnvironmentVariable} must be supplied by Electron for every backend launch.");
		}

		return app.UseMiddleware<LaunchTokenMiddleware>(token);
	}
}
