using System.Security.Cryptography;

namespace MyloMail.Api.Security;

/// <summary>Rejects local callers that do not possess the token Electron generated for this launch (§9).</summary>
public sealed class LaunchTokenMiddleware(RequestDelegate next, string expectedToken)
{
	public async Task InvokeAsync(HttpContext context)
	{
		var candidate = Supplied(context);
		if (candidate is null)
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

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

	/// <summary>
	/// The token from the <c>Authorization</c> header, or from the query string.
	/// </summary>
	/// <remarks>
	/// A WebSocket handshake cannot carry custom headers, which is why SignalR clients pass the
	/// token as <c>access_token</c> — §9's access-token factory. Both forms are accepted, and
	/// both are compared the same way; refusing the query form would mean the hub could not be
	/// authenticated at all, and adding an exemption for it would mean it was not.
	/// </remarks>
	private static string? Supplied(HttpContext context)
	{
		const string scheme = "Bearer ";
		var header = context.Request.Headers.Authorization.ToString();
		if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
		{
			return header[scheme.Length..];
		}

		return context.Request.Query.TryGetValue("access_token", out var queryToken)
			? queryToken.ToString()
			: null;
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
