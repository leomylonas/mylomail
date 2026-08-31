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

	/// <summary>The cookie the shell sets for this launch, before it loads anything.</summary>
	public const string CookieName = "mylomail_launch";

	/// <summary>
	/// The token from the <c>Authorization</c> header, or from the launch cookie.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The cookie exists because a WebSocket handshake cannot carry custom headers. SignalR's
	/// usual answer is an <c>access_token</c> query parameter, which is deliberately <b>not</b>
	/// accepted here: a URL is the most quotable thing in any system, ending up in logs, crash
	/// reports and referrers, and this token is the backend's only defence against another
	/// local process.
	/// </para>
	/// <para>
	/// A cookie works only because the renderer is served from this origin, which is why it is
	/// served from here rather than from <c>file://</c>. Same-origin requests — documents,
	/// assets, fetches and the WebSocket handshake alike — carry it automatically, so the
	/// renderer never has to hold the token at all.
	/// </para>
	/// </remarks>
	private static string? Supplied(HttpContext context)
	{
		const string scheme = "Bearer ";
		var header = context.Request.Headers.Authorization.ToString();
		if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
		{
			return header[scheme.Length..];
		}

		return context.Request.Cookies.TryGetValue(CookieName, out var cookie) ? cookie : null;
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
