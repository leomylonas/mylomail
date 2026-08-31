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
	/// A WebSocket handshake cannot carry custom headers, so SignalR passes the token as a
	/// query parameter. Rejecting that form would mean the hub could not be authenticated.
	/// </summary>
	[Fact]
	public async Task An_access_token_query_parameter_is_accepted()
	{
		var context = Request(query: Token);

		Assert.True(await InvokeAsync(context));
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData("Bearer wrong", null)]
	[InlineData(null, "wrong")]
	[InlineData("Basic " + Token, null)]
	public async Task Anything_else_is_rejected(string? headers, string? query)
	{
		var context = Request(headers, query);

		Assert.False(await InvokeAsync(context));
		Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
	}

	private static DefaultHttpContext Request(string? headers = null, string? query = null)
	{
		var context = new DefaultHttpContext();
		if (headers is not null) context.Request.Headers.Authorization = headers;
		if (query is not null) context.Request.QueryString = new QueryString($"?access_token={query}");
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
