using Microsoft.AspNetCore.Http;
using MyloMail.Api.Security;
using Xunit;

namespace MyloMail.Api.Tests.Security;

/// <summary>
/// The per-launch token is the backend's only defence against another local process (§9).
/// </summary>
public sealed class LaunchTokenMiddlewareTests
{
	private const string Token = "the-launch-token";

	[Fact]
	public async Task A_bearer_header_is_accepted()
	{
		var context = Request(headers: $"Bearer {Token}");

		Assert.True(await InvokeAsync(context));
	}

	/// <summary>
	/// A WebSocket handshake cannot carry custom headers, so the renderer authenticates with a
	/// cookie the shell sets before anything loads. It works only because the renderer is
	/// served from this origin.
	/// </summary>
	[Fact]
	public async Task The_launch_cookie_is_accepted()
	{
		var context = Request(cookie: Token);

		Assert.True(await InvokeAsync(context));
	}

	/// <summary>
	/// SignalR's usual answer is an <c>access_token</c> query parameter, and it is refused. A
	/// URL is the most quotable thing in a system — logs, crash reports, referrers — and this
	/// token is the only thing standing between another local process and the user's mail.
	/// </summary>
	[Fact]
	public async Task An_access_token_query_parameter_is_refused()
	{
		var context = Request(query: Token);

		Assert.False(await InvokeAsync(context));
	}

	[Theory]
	[InlineData(null, null, null)]
	[InlineData("Bearer wrong", null, null)]
	[InlineData(null, null, "wrong")]
	[InlineData("Basic " + Token, null, null)]
	public async Task Anything_else_is_rejected(string? headers, string? query, string? cookie)
	{
		var context = Request(headers, query, cookie);

		Assert.False(await InvokeAsync(context));
		Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
	}

	private static DefaultHttpContext Request(
		string? headers = null,
		string? query = null,
		string? cookie = null
	)
	{
		var context = new DefaultHttpContext();
		if (headers is not null) context.Request.Headers.Authorization = headers;
		if (query is not null) context.Request.QueryString = new QueryString($"?access_token={query}");
		if (cookie is not null)
		{
			context.Request.Headers.Cookie = $"{LaunchTokenMiddleware.CookieName}={cookie}";
		}

		return context;
	}

	private static async Task<bool> InvokeAsync(HttpContext context)
	{
		var reached = false;
		var middleware = new LaunchTokenMiddleware(
			_ =>
			{
				reached = true;
				return Task.CompletedTask;
			},
			Token
		);

		await middleware.InvokeAsync(context);
		return reached;
	}
}
