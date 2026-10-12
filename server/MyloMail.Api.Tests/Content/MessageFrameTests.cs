using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Content;
using MyloMail.Api.Controllers;
using Xunit;

namespace MyloMail.Api.Tests.Content;

/// <summary>The policy a message document is served under is the isolation, so it is pinned here.</summary>
public sealed class MessageFrameTests
{
	private static MessageFramesController Controller(MessageFrameStore store) =>
		new(store) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

	[Fact]
	public void A_document_is_served_under_a_policy_that_allows_inline_styles_but_never_script_or_remote_loads()
	{
		var store = new MessageFrameStore(TimeProvider.System);
		var controller = Controller(store);
		var created = Assert.IsType<OkObjectResult>(controller.Create(new MessageFrameRequest("<p style=\"color:red\">hi</p>", false)));
		var id = (string)created.Value!.GetType().GetProperty("id")!.GetValue(created.Value)!;

		var served = Assert.IsType<ContentResult>(controller.Get(id));

		Assert.Equal("<p style=\"color:red\">hi</p>", served.Content);
		var policy = controller.Response.Headers.ContentSecurityPolicy.ToString();
		Assert.Contains("style-src 'unsafe-inline'", policy);
		Assert.Contains("script-src 'none'", policy);
		Assert.Contains("default-src 'none'", policy);
		Assert.Contains("sandbox allow-same-origin", policy);
		Assert.Contains("frame-ancestors 'self'", policy);
		Assert.DoesNotContain("http:", policy);
		Assert.DoesNotContain("https:", policy);
		Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
	}

	[Fact]
	public void Remote_images_are_allowed_only_when_the_user_allowed_them_for_that_message()
	{
		Assert.Contains("img-src blob: data: https: http:", MessageFramesController.Policy(allowRemote: true));
		Assert.DoesNotContain("https:", MessageFramesController.Policy(allowRemote: false));
	}

	[Fact]
	public void An_unknown_or_expired_id_is_not_found()
	{
		var clock = new FakeTimeProvider();
		var store = new MessageFrameStore(clock);
		var id = store.Add("<p>x</p>", false);

		Assert.IsType<NotFoundResult>(Controller(store).Get("not-an-id"));

		clock.Advance(MessageFrameStore.Lifetime + TimeSpan.FromSeconds(1));
		Assert.IsType<NotFoundResult>(Controller(store).Get(id));
	}

	[Fact]
	public void The_store_is_bounded_and_drops_the_oldest_first()
	{
		var clock = new FakeTimeProvider();
		var store = new MessageFrameStore(clock);
		var first = store.Add("first", false);
		for (var i = 0; i < MessageFrameStore.MaxEntries + 5; i++)
		{
			clock.Advance(TimeSpan.FromSeconds(1));
			store.Add($"doc {i}", false);
		}

		Assert.Null(store.Get(first));
	}

	[Fact]
	public void An_empty_document_is_refused()
	{
		var controller = Controller(new MessageFrameStore(TimeProvider.System));

		var result = controller.Create(new MessageFrameRequest("", false));

		Assert.IsType<ObjectResult>(result);
		Assert.Equal(400, ((ObjectResult)result).StatusCode);
	}
}
