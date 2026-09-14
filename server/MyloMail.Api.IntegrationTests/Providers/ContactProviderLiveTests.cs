using System.Net;
using System.Net.Http.Headers;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
[Trait("Category", "LiveProvider")]
public abstract class ContactProviderLiveTests
{
	protected abstract string? SkipReason { get; }
	protected abstract bool SupportsIncrementalPull { get; }
	protected abstract bool SupportsProviderDelete { get; }
	protected abstract Task<ContactLiveSubject> CreateSubjectAsync();

	[SkippableFact]
	public async Task A_contact_can_be_created_pulled_updated_conflicted_and_deleted_when_supported()
	{
		Skip.If(SkipReason is not null, SkipReason ?? string.Empty);
		using var subject = await CreateSubjectAsync();
		var baseline = await subject.Provider.PullAsync(
			subject.Account,
			useCursor: false,
			CancellationToken.None
		);
		Assert.True(baseline.IsFullSnapshot);
		if (SupportsIncrementalPull)
		{
			Assert.False(string.IsNullOrWhiteSpace(baseline.NextCursor));
		}

		var key = Guid.NewGuid().ToString("N");
		var original = new ProviderContactWrite(
			$"MyloMail contact live {key}",
			[$"mylomail.live.{key}@example.com"]
		);
		ProviderContact? cleanup = null;
		try
		{
			var created = await subject.Provider.CreateAsync(
				subject.Account,
				original,
				CancellationToken.None
			);
			cleanup = created;
			Assert.False(string.IsNullOrWhiteSpace(created.ProviderContactId));
			Assert.False(string.IsNullOrWhiteSpace(created.Revision));
			Assert.Equal(original.DisplayName, created.DisplayName);
			Assert.Equal(original.Emails, created.Emails);

			var observedCreate = await WaitForContactAsync(
				subject,
				SupportsIncrementalPull,
				baseline.NextCursor,
				contact => contact.ProviderContactId == created.ProviderContactId
					&& contact.DisplayName == original.DisplayName,
				"created contact"
			);
			Assert.Equal(!SupportsIncrementalPull, observedCreate.Pull.IsFullSnapshot);

			var staleRevision = created.Revision;
			var updatedWrite = new ProviderContactWrite(
				$"{original.DisplayName} updated",
				[original.Emails[0], $"mylomail.alt.{key}@example.com"]
			);
			var updated = await subject.Provider.UpdateAsync(
				subject.Account,
				created.ProviderContactId,
				created.ProviderContainerId,
				staleRevision,
				updatedWrite,
				CancellationToken.None
			);
			cleanup = updated;
			Assert.False(string.IsNullOrWhiteSpace(updated.Revision));
			Assert.NotEqual(staleRevision, updated.Revision);
			Assert.Equal(updatedWrite.DisplayName, updated.DisplayName);
			Assert.Equal(updatedWrite.Emails, updated.Emails);

			await Assert.ThrowsAsync<ProviderConflictException>(() =>
				subject.Provider.UpdateAsync(
					subject.Account,
					updated.ProviderContactId,
					updated.ProviderContainerId,
					staleRevision,
					new ProviderContactWrite(
						$"{original.DisplayName} stale overwrite",
						updatedWrite.Emails
					),
					CancellationToken.None
				)
			);

			var observedUpdate = await WaitForContactAsync(
				subject,
				useCursor: false,
				cursor: null,
				contact => contact.ProviderContactId == updated.ProviderContactId
					&& contact.DisplayName == updatedWrite.DisplayName
					&& contact.Emails.ToHashSet(StringComparer.OrdinalIgnoreCase)
						.SetEquals(updatedWrite.Emails),
				"updated contact"
			);
			Assert.DoesNotContain(
				observedUpdate.Pull.Contacts,
				contact => contact.ProviderContactId == updated.ProviderContactId
					&& contact.DisplayName.EndsWith("stale overwrite", StringComparison.Ordinal)
			);

			if (!SupportsProviderDelete)
			{
				await Assert.ThrowsAsync<ProviderContactRejectedException>(() =>
					subject.Provider.DeleteAsync(
						subject.Account,
						updated.ProviderContactId,
						updated.ProviderContainerId,
						updated.Revision,
						CancellationToken.None
					)
				);
				return;
			}

			await subject.Provider.DeleteAsync(
				subject.Account,
				updated.ProviderContactId,
				updated.ProviderContainerId,
				updated.Revision,
				CancellationToken.None
			);
			cleanup = null;
			await WaitUntilMissingAsync(subject, updated.ProviderContactId);
		}
		finally
		{
			if (cleanup is not null)
			{
				await subject.CleanupAsync(cleanup, CancellationToken.None);
			}
		}
	}

	private static async Task<ContactObservation> WaitForContactAsync(
		ContactLiveSubject subject,
		bool useCursor,
		string? cursor,
		Func<ProviderContact, bool> predicate,
		string operation
	)
	{
		var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
		do
		{
			subject.Account.ContactSyncCursor = cursor;
			var pull = await subject.Provider.PullAsync(
				subject.Account,
				useCursor,
				CancellationToken.None
			);
			var contact = pull.Contacts.FirstOrDefault(predicate);
			if (contact is not null)
			{
				return new ContactObservation(pull, contact);
			}
			await Task.Delay(TimeSpan.FromSeconds(2));
		}
		while (DateTimeOffset.UtcNow < deadline);

		throw new TimeoutException($"The provider did not surface the {operation} within one minute.");
	}

	private static async Task WaitUntilMissingAsync(
		ContactLiveSubject subject,
		string providerContactId
	)
	{
		var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
		do
		{
			var pull = await subject.Provider.PullAsync(
				subject.Account,
				useCursor: false,
				CancellationToken.None
			);
			if (pull.Contacts.All(contact => contact.ProviderContactId != providerContactId))
			{
				return;
			}
			await Task.Delay(TimeSpan.FromSeconds(2));
		}
		while (DateTimeOffset.UtcNow < deadline);

		throw new TimeoutException("The provider did not surface the deleted contact within one minute.");
	}

	protected sealed class ContactLiveSubject(
		IContactProvider provider,
		Account account,
		HttpClient client,
		Func<ProviderContact, CancellationToken, Task> cleanup
	) : IDisposable
	{
		public IContactProvider Provider { get; } = provider;
		public Account Account { get; } = account;
		public Func<ProviderContact, CancellationToken, Task> CleanupAsync { get; } = cleanup;

		public void Dispose() => client.Dispose();
	}

	private sealed record ContactObservation(ContactPullResult Pull, ProviderContact Contact);
}

[Collection(LiveProviderTestCollections.Gmail)]
[Trait("Provider", "Gmail")]
[Trait("Category", "LiveProvider")]
[Trait("Area", "Contacts")]
public sealed class GoogleContactProviderLiveTests : ContactProviderLiveTests
{
	protected override string? SkipReason => ProviderLiveTestContext.GmailSkipReason;
	protected override bool SupportsIncrementalPull => true;
	protected override bool SupportsProviderDelete => false;

	protected override async Task<ContactLiveSubject> CreateSubjectAsync()
	{
		var context = await ProviderLiveTestContext.GmailAsync();
		var client = new HttpClient();
		var provider = new GooglePeopleContactProvider(context.OAuth, client);
		return new ContactLiveSubject(
			provider,
			context.Account,
			client,
			async (contact, ct) =>
			{
				var credential = await context.OAuth.AuthorizeAsync(
					context.Account,
					requireCalendarScope: false,
					requireContactsScope: true,
					ct
				);
				var token = await credential.GetAccessTokenForRequestAsync();
				var path = string.Join(
					"/",
					contact.ProviderContactId.Split('/').Select(Uri.EscapeDataString)
				);
				using var request = new HttpRequestMessage(
					HttpMethod.Delete,
					$"https://people.googleapis.com/v1/{path}:deleteContact"
				);
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
				using var response = await client.SendAsync(request, ct);
				if (response.StatusCode != HttpStatusCode.NotFound)
				{
					response.EnsureSuccessStatusCode();
				}
			}
		);
	}
}

[Collection(LiveProviderTestCollections.Graph)]
[Trait("Provider", "Graph")]
[Trait("Category", "LiveProvider")]
[Trait("Area", "Contacts")]
public sealed class GraphContactProviderLiveTests : ContactProviderLiveTests
{
	protected override string? SkipReason => ProviderLiveTestContext.GraphSkipReason;
	protected override bool SupportsIncrementalPull => false;
	protected override bool SupportsProviderDelete => true;

	protected override async Task<ContactLiveSubject> CreateSubjectAsync()
	{
		var context = await ProviderLiveTestContext.GraphAsync();
		var client = new HttpClient();
		var provider = new GraphContactProvider(context.OAuth, client);
		return new ContactLiveSubject(
			provider,
			context.Account,
			client,
			(contact, ct) => provider.DeleteAsync(
				context.Account,
				contact.ProviderContactId,
				contact.ProviderContainerId,
				expectedRevision: null,
				ct
			)
		);
	}
}
