using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Content;
using MyloMail.Api.Contracts;
using MyloMail.Api.Controllers;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Content;

/// <summary>The prepared message and the policy it is served under are the isolation, so both are pinned here.</summary>
public sealed class MessageHtmlControllerTests
{
	private const string Png =
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

	private static async Task<Guid> SeedAsync(
		TestDatabase database,
		string html,
		string sender = "sender@example.org",
		string? raw = null
	)
	{
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var accountId = Guid.NewGuid();
		var messageId = Guid.NewGuid();
		context.Accounts.Add(new Account { Id = accountId, DisplayName = "A", ProviderType = ProviderType.Imap });
		context.Messages.Add(new Message { Id = messageId, AccountId = accountId, From = [new Address("Sender", sender)] });
		context.MessageBodies.Add(new MessageBody { MessageId = messageId, HtmlBody = html });
		if (raw is not null)
		{
			context.MessageRaws.Add(new MessageRaw { MessageId = messageId, Content = System.Text.Encoding.ASCII.GetBytes(raw) });
		}

		await context.SaveChangesAsync();
		return messageId;
	}

	private static MessageHtmlController Controller(IServiceScope scope) =>
		new(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>())
		{
			ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
		};

	[Fact]
	public async Task A_message_is_served_under_a_policy_that_allows_inline_styles_but_never_script_or_remote_loads()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var id = await SeedAsync(database, "<p style=\"color:red\">hi</p><img src=\"https://tracker.example/p.gif\">");
		await using var scope = database.CreateScope();
		var controller = Controller(scope);

		var served = Assert.IsType<ContentResult>(await controller.Frame(id, remote: false, dark: false, invert: false, CancellationToken.None));

		Assert.Contains("hi", served.Content);
		Assert.DoesNotContain("tracker.example", served.Content);
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

	private static T Value<T>(ActionResult<T> result) =>
		Assert.IsType<T>(result.Value ?? Assert.IsType<OkObjectResult>(result.Result).Value);

	[Fact]
	public async Task The_readers_request_loads_remote_content_for_that_view_and_widens_the_policy_to_match()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var id = await SeedAsync(database, "<img src=\"https://tracker.example/p.gif\">");
		await using var scope = database.CreateScope();
		var controller = Controller(scope);

		var held = Value(await controller.Info(id, remote: false, CancellationToken.None));
		Assert.Equal(1, held.BlockedRemoteCount);
		Assert.False(held.RemoteAllowed);
		Assert.Equal(RemoteContentDecision.Default, held.Decision);

		var loaded = Value(await controller.Info(id, remote: true, CancellationToken.None));
		Assert.True(loaded.RemoteAllowed);
		Assert.Equal(0, loaded.BlockedRemoteCount);
		Assert.Contains("tracker.example", Value(await controller.Print(id, remote: true, CancellationToken.None)).Html);

		await controller.Frame(id, remote: true, dark: false, invert: false, CancellationToken.None);
		Assert.Contains("img-src data: https: http:", controller.Response.Headers.ContentSecurityPolicy.ToString());
	}

	[Fact]
	public async Task A_senders_rule_decides_whatever_the_reader_asks()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var blocked = await SeedAsync(database, "<img src=\"https://tracker.example/p.gif\">", "tracker@example.org");
		var allowed = await SeedAsync(database, "<img src=\"https://tracker.example/p.gif\">", "friend@trusted.example");
		await using (var seed = database.CreateScope())
		{
			var context = seed.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.RemoteContentRules.Add(new RemoteContentRule { Id = Guid.NewGuid(), Scope = RemoteContentRuleScope.Sender, Decision = RemoteContentRuleDecision.Block, Value = "tracker@example.org" });
			context.RemoteContentRules.Add(new RemoteContentRule { Id = Guid.NewGuid(), Scope = RemoteContentRuleScope.Domain, Decision = RemoteContentRuleDecision.Allow, Value = "trusted.example" });
			await context.SaveChangesAsync();
		}

		await using var scope = database.CreateScope();
		var controller = Controller(scope);

		var block = Value(await controller.Info(blocked, remote: true, CancellationToken.None));
		Assert.Equal(RemoteContentDecision.Block, block.Decision);
		Assert.False(block.RemoteAllowed);
		Assert.DoesNotContain("tracker.example", Value(await controller.Print(blocked, remote: true, CancellationToken.None)).Html);

		var allow = Value(await controller.Info(allowed, remote: false, CancellationToken.None));
		Assert.Equal(RemoteContentDecision.Allow, allow.Decision);
		Assert.True(allow.RemoteAllowed);
		Assert.Contains("tracker.example", Value(await controller.Print(allowed, remote: false, CancellationToken.None)).Html);
	}

	[Fact]
	public async Task An_inline_image_is_embedded_from_the_stored_message_and_is_not_remote()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var raw = string.Join(
			"\r\n",
			"From: Someone <sender@example.org>", "To: me@example.org", "Subject: s", "MIME-Version: 1.0",
			"Content-Type: multipart/related; boundary=\"b\"", "",
			"--b", "Content-Type: text/html; charset=utf-8", "", "<img src=\"cid:inline-image@example.org\">", "",
			"--b", "Content-Type: image/png", "Content-Transfer-Encoding: base64", "Content-ID: <inline-image@example.org>", "", Png, "",
			"--b--", ""
		);
		var id = await SeedAsync(database, "<img id=\"inline\" src=\"cid:inline-image@example.org\">", raw: raw);
		await using var scope = database.CreateScope();
		var controller = Controller(scope);

		var served = Assert.IsType<ContentResult>(await controller.Frame(id, remote: false, dark: false, invert: false, CancellationToken.None));

		Assert.Contains($"src=\"data:image/png;base64,{Png}\"", served.Content);
		Assert.DoesNotContain("cid:", served.Content);
		Assert.Equal(0, Value(await controller.Info(id, remote: false, CancellationToken.None)).BlockedRemoteCount);
	}

	/// <summary>
	/// A reply quotes the message into an editor, where its inline images are re-attached by Content-ID:
	/// the references stay, and remote content stays withheld.
	/// </summary>
	[Fact]
	public async Task A_quoted_message_keeps_its_cid_references_and_never_loads_remote_content()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var id = await SeedAsync(database, "<img src=\"cid:logo@example.org\"><img src=\"https://tracker.example/p.gif\"><script>alert(1)</script>", "friend@trusted.example");
		await using (var seed = database.CreateScope())
		{
			var context = seed.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.RemoteContentRules.Add(new RemoteContentRule { Id = Guid.NewGuid(), Scope = RemoteContentRuleScope.Domain, Decision = RemoteContentRuleDecision.Allow, Value = "trusted.example" });
			await context.SaveChangesAsync();
		}

		await using var scope = database.CreateScope();
		var quoted = Value(await Controller(scope).Quote(id, CancellationToken.None));

		Assert.Contains("cid:logo@example.org", quoted.Html);
		Assert.DoesNotContain("tracker.example", quoted.Html);
		Assert.DoesNotContain("<script", quoted.Html, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The print layout is in the app's own page, which refuses style elements and attributes in markup
	/// and reports each one: they come separately, for the renderer to apply as CSS.
	/// </summary>
	[Fact]
	public async Task The_print_copy_has_its_styles_separated_from_its_markup()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var id = await SeedAsync(database, "<style>.a { color: red }</style><p class=\"a\" style=\"margin: 0\">hi</p><div style=\"color: blue\">x</div>");
		await using var scope = database.CreateScope();

		var print = Value(await Controller(scope).Print(id, remote: false, CancellationToken.None));

		Assert.DoesNotContain("<style", print.Html);
		Assert.DoesNotContain(" style=", print.Html);
		Assert.Single(print.Sheets);
		Assert.Contains(".a", print.Sheets[0]);
		Assert.Equal(2, print.Inline.Count);
		Assert.Contains(print.Inline, pair => pair.Value.Contains("margin") && print.Html.Contains($"data-inline-style=\"{pair.Key}\""));
		Assert.Contains(print.Inline, pair => pair.Value.Contains("blue") || pair.Value.Contains("0, 0, 255"));
	}

	[Fact]
	public async Task A_message_with_no_html_has_nothing_to_prepare()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var id = await SeedAsync(database, "");
		await using var scope = database.CreateScope();
		var controller = Controller(scope);

		Assert.Equal(404, Assert.IsType<ObjectResult>((await controller.Info(id, remote: false, CancellationToken.None)).Result).StatusCode);
		Assert.Equal(404, Assert.IsType<ObjectResult>((await controller.Quote(id, CancellationToken.None)).Result).StatusCode);
		Assert.Equal(404, Assert.IsType<ObjectResult>((await controller.Print(id, remote: false, CancellationToken.None)).Result).StatusCode);
		Assert.IsType<NotFoundResult>(await controller.Frame(id, false, false, false, CancellationToken.None));
		Assert.IsType<NotFoundResult>(await controller.Frame(Guid.NewGuid(), false, false, false, CancellationToken.None));
	}

	[Fact]
	public void The_policy_allows_remote_images_only_when_asked()
	{
		Assert.Contains("img-src data: https: http:", MessageHtmlController.Policy(allowRemote: true));
		Assert.DoesNotContain("https:", MessageHtmlController.Policy(allowRemote: false));
	}
}
