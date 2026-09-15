using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Content;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Sixty-fourth pass, closing a second invariant-review gap in the pass's own fix:
/// <see cref="ContentAcquisition.AcquireAsync"/>'s generic fetch-failure catch counted a
/// <see cref="CredentialStoreUnavailableException"/> against <see cref="ContentAcquisition.MaxAttempts"/>
/// the same as a genuinely malformed message, so a sustained local credential-store outage
/// could permanently mark readable content <see cref="ContentStatus.Failed"/> after five
/// restarts — nothing about the message was ever actually unreadable.
/// </summary>
public sealed class ContentAcquisitionCredentialStoreTests
{
	[Fact]
	public async Task A_credential_store_failure_does_not_consume_the_retry_budget()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMailboxAsync(harness);
		var messageId = await SeedMessageAsync(harness, mailboxId);
		harness.Provider.FailFetchRawMessageWith(new CredentialStoreUnavailableException("the keyring is locked"));

		await harness.UsingAsync(async scope =>
		{
			var acquisition = scope.GetRequiredService<ContentAcquisition>();
			var account = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			await Assert.ThrowsAsync<CredentialStoreUnavailableException>(
				() => acquisition.AcquireAsync(account, messageId, CancellationToken.None)
			);
		});

		var state = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().MessageContentStates.SingleAsync(c => c.MessageId == messageId)
		);
		Assert.Equal(0, state.Attempts);
		Assert.Equal(ContentStatus.Queued, state.Status);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Account_access_failures_do_not_consume_the_content_retry_budget(
		bool providerNotConfigured
	)
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMailboxAsync(harness);
		var messageId = await SeedMessageAsync(harness, mailboxId);
		Exception failure = providerNotConfigured
			? new ProviderNotConfiguredException(ProviderType.Gmail, "Providers:Gmail:ClientId")
			: new ProviderAuthenticationException("reauthenticate");
		harness.Provider.FailFetchRawMessageWith(failure);

		await harness.UsingAsync(async scope =>
		{
			var acquisition = scope.GetRequiredService<ContentAcquisition>();
			var account = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			await Assert.ThrowsAsync(failure.GetType(), () =>
				acquisition.AcquireAsync(account, messageId, CancellationToken.None)
			);
		});

		var state = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().MessageContentStates.SingleAsync(c =>
				c.MessageId == messageId
			)
		);
		Assert.Equal(0, state.Attempts);
		Assert.Equal(ContentStatus.Queued, state.Status);
	}

	[Fact]
	public async Task Credential_store_failure_reschedules_content_acquisition()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMailboxAsync(harness);
		await SeedMessageAsync(harness, mailboxId);
		harness.Provider.FailFetchRawMessageWith(
			new CredentialStoreUnavailableException("the keyring is locked")
		);

		await harness.UsingAsync(async scope =>
		{
			var recorder =
				(RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();

			await scope.GetRequiredService<ContentJobs>().FetchNextAsync(harness.Account.Id);

			Assert.IsType<ScheduledState>(Assert.Single(recorder.States));
			var account = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			Assert.Equal(AuthState.CredentialStoreUnavailable, account.AuthState);
		});
	}

	[Theory]
	[InlineData(false, AuthState.NeedsReauth)]
	[InlineData(true, AuthState.Error)]
	public async Task Account_access_failure_pauses_content_job_without_self_rescheduling(
		bool providerNotConfigured,
		AuthState expectedState
	)
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = await SeedMailboxAsync(harness);
		await SeedMessageAsync(harness, mailboxId);
		Exception failure = providerNotConfigured
			? new ProviderNotConfiguredException(ProviderType.Gmail, "Providers:Gmail:ClientId")
			: new ProviderAuthenticationException("reauthenticate");
		harness.Provider.FailFetchRawMessageWith(failure);

		await harness.UsingAsync(async scope =>
		{
			var recorder =
				(RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();

			await scope.GetRequiredService<ContentJobs>().FetchNextAsync(harness.Account.Id);

			Assert.Empty(recorder.Created);
			var account = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			Assert.Equal(expectedState, account.AuthState);
		});
		Assert.Equal(expectedState, Assert.Single(harness.Events.AccountStatuses).AuthState);
	}

	private static async Task<Guid> SeedMailboxAsync(SyncHarness harness)
	{
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = harness.Account.Id,
					ProviderMailboxId = "INBOX",
					Name = "Inbox",
					SpecialUse = SpecialUse.Inbox,
				}
			);
			await context.SaveChangesAsync();
		});
		return mailboxId;
	}

	private static async Task<Guid> SeedMessageAsync(SyncHarness harness, Guid mailboxId)
	{
		var messageId = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.Account.Id,
					ReceivedAt = DateTimeOffset.UnixEpoch,
					Occurrences =
					[
						new MessageMailbox
						{
							Id = Guid.NewGuid(),
							MailboxId = mailboxId,
							ProviderOccurrenceId = messageId.ToString(),
						},
					],
				}
			);
			context.MessageContentStates.Add(
				new MessageContentState
				{
					MessageId = messageId,
					Status = ContentStatus.Queued,
				}
			);
			await context.SaveChangesAsync();
		});
		return messageId;
	}
}
