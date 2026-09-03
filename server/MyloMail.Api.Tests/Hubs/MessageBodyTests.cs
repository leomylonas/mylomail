using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// Seventy-ninth architecture-review pass: <see cref="MailHub.GetMessageBody"/> returned the
/// same all-null shape for "not yet fetched" and "this message no longer exists" (deleted, or
/// its whole account removed) — a reading pane left open on it would poll forever every two
/// seconds, showing a blank body under a stale subject line with no indication anything was
/// wrong.
/// </summary>
public sealed class MessageBodyTests
{
	[Fact]
	public async Task Fetching_the_body_of_a_message_that_no_longer_exists_throws()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);

		await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			await Assert.ThrowsAsync<HubException>(() => hub.GetMessageBody(Guid.NewGuid()));
		});
	}
}
