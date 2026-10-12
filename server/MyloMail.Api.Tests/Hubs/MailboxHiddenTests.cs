using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

public sealed class MailboxHiddenTests
{
	[Fact]
	public async Task Hiding_a_folder_is_local_and_is_reported_on_its_summary_until_it_is_shown_again()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox { Id = mailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "Sync Issues", Name = "Sync Issues" });
			await context.SaveChangesAsync();
		});

		async Task<bool> HiddenAsync() =>
			await harness.UsingAsync(async services =>
				(await MailboxSummaryDtoFactory.GetAsync(services.GetRequiredService<MyloMailDbContext>(), mailboxId))!.IsHidden);

		Assert.False(await HiddenAsync());
		await harness.UsingAsync(services => services.GetRequiredService<MailHub>().SetMailboxHidden(mailboxId, true));
		Assert.True(await HiddenAsync());
		await harness.UsingAsync(services => services.GetRequiredService<MailHub>().SetMailboxHidden(mailboxId, false));
		Assert.False(await HiddenAsync());
	}
}
