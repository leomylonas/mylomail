using MyloMail.Api.Credentials;
using Xunit;

namespace MyloMail.Api.Tests.Credentials;

public sealed class InMemoryCredentialStoreTests
{
	[Fact]
	public async Task Does_not_expose_a_mutable_payload_buffer()
	{
		var store = new InMemoryCredentialStore();
		var accountId = Guid.NewGuid();
		var source = new byte[] { 1, 2, 3 };

		await store.StoreAsync(accountId, new CredentialPayload("test", source), CancellationToken.None);
		source[0] = 9;
		var payload = await store.RetrieveAsync(accountId, CancellationToken.None);
		Assert.NotNull(payload);
		Assert.Equal(new byte[] { 1, 2, 3 }, payload.Data);

		payload.Data[1] = 8;
		var retrievedAgain = await store.RetrieveAsync(accountId, CancellationToken.None);
		Assert.NotNull(retrievedAgain);
		Assert.Equal(new byte[] { 1, 2, 3 }, retrievedAgain.Data);
	}
}
