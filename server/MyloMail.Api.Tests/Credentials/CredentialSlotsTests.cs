using MyloMail.Api.Credentials;
using Xunit;

namespace MyloMail.Api.Tests.Credentials;

public sealed class CredentialSlotsTests
{
	[Fact]
	public async Task Secondary_slots_are_independent_without_changing_the_primary_credential()
	{
		var accountId = Guid.NewGuid();
		var store = new InMemoryCredentialStore();
		await store.StoreAsync(accountId, new CredentialPayload("imap", [1]), default);
		await store.StoreSlotAsync(accountId, CredentialSlots.CalDav, new CredentialPayload("basic", [2]), default);

		Assert.Equal([1], (await store.RetrieveAsync(accountId, default))!.Data);
		Assert.Equal([2], (await store.RetrieveSlotAsync(accountId, CredentialSlots.CalDav, default))!.Data);
		Assert.NotEqual(accountId, CredentialSlots.Key(accountId, CredentialSlots.CalDav));
	}
}
