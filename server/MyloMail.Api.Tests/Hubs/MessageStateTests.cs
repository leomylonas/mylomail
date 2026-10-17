using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

public sealed class MessageStateTests
{
	[Fact]
	public async Task A_change_still_on_its_way_shows_before_the_server_has_confirmed_it()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var messageId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(new Message { Id = messageId, AccountId = harness.Account.Id, IsRead = true, IsFlagged = false });
			await context.SaveChangesAsync();
		});
		Task<MyloMail.Api.Contracts.MessageStateDto> StateAsync() =>
			harness.UsingAsync(services => services.GetRequiredService<MailHub>().GetMessageState(messageId));

		Assert.Equal((true, false), ((await StateAsync()).IsRead, (await StateAsync()).IsFlagged));

		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetFlags(harness.Account.Id, [messageId], false, true));

		var state = await StateAsync();
		Assert.False(state.IsRead);
		Assert.True(state.IsFlagged);
	}
}
