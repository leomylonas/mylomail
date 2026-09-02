using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// Fortieth architecture-review pass: §13 Epic 4 requires a failed mutation be shown via a
/// durable indicator on the affected message, not just the one-shot <c>MessageSyncFailed</c>
/// toast. <see cref="MessageMutationFailures"/> is the read-side lookup that backs it.
/// </summary>
public sealed class MessageMutationFailuresTests
{
	[Fact]
	public async Task A_message_whose_latest_mutation_failed_is_reported()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await UsingAsync(
			database,
			async context =>
			{
				context.MutationItems.Add(
					Item(accountId, messageId, sequence: 1, MutationState.Failed, ErrorCategory.Validation)
				);
				await context.SaveChangesAsync();
				return true;
			}
		);

		await UsingAsync(
			database,
			async context =>
			{
				var failures = await MessageMutationFailures.ForMessagesAsync(context, [messageId]);
				Assert.Equal(ErrorCategory.Validation, failures[messageId]);
				return true;
			}
		);
	}

	/// <summary>
	/// Self-clearing: a later successful mutation outranks the earlier failed one by
	/// sequence, with no separate flag to reset.
	/// </summary>
	[Fact]
	public async Task A_later_successful_mutation_clears_an_earlier_failure()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await UsingAsync(
			database,
			async context =>
			{
				context.MutationItems.Add(
					Item(accountId, messageId, sequence: 1, MutationState.Failed, ErrorCategory.Validation)
				);
				context.MutationItems.Add(
					Item(accountId, messageId, sequence: 2, MutationState.Completed, null)
				);
				await context.SaveChangesAsync();
				return true;
			}
		);

		await UsingAsync(
			database,
			async context =>
			{
				var failures = await MessageMutationFailures.ForMessagesAsync(context, [messageId]);
				Assert.False(failures.ContainsKey(messageId));
				return true;
			}
		);
	}

	/// <summary>
	/// A newer mutation that is merely queued does not resolve anything yet — the failure it
	/// may or may not supersede is still the truth until that new mutation itself finishes.
	/// </summary>
	[Fact]
	public async Task A_merely_queued_newer_mutation_does_not_hide_an_unresolved_failure()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await UsingAsync(
			database,
			async context =>
			{
				context.MutationItems.Add(
					Item(accountId, messageId, sequence: 1, MutationState.Failed, ErrorCategory.Validation)
				);
				context.MutationItems.Add(
					Item(accountId, messageId, sequence: 2, MutationState.Pending, null)
				);
				await context.SaveChangesAsync();
				return true;
			}
		);

		await UsingAsync(
			database,
			async context =>
			{
				var failures = await MessageMutationFailures.ForMessagesAsync(context, [messageId]);
				Assert.Equal(ErrorCategory.Validation, failures[messageId]);
				return true;
			}
		);
	}

	[Fact]
	public async Task A_message_with_no_mutations_is_not_reported()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (_, messageId) = await SeedAsync(database);

		await UsingAsync(
			database,
			async context =>
			{
				var failures = await MessageMutationFailures.ForMessagesAsync(context, [messageId]);
				Assert.False(failures.ContainsKey(messageId));
				return true;
			}
		);
	}

	private static MutationItem Item(
		Guid accountId,
		Guid messageId,
		long sequence,
		MutationState state,
		ErrorCategory? failureCategory
	) =>
		new()
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			MessageId = messageId,
			Sequence = sequence,
			OperationKind = MutationOperationKind.SetFlags,
			State = state,
			CreatedAt = DateTimeOffset.UnixEpoch,
			FailureCategory = failureCategory,
		};

	private static async Task<T> UsingAsync<T>(TestDatabase database, Func<MyloMailDbContext, Task<T>> work)
	{
		await using var scope = database.CreateScope();
		return await work(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>());
	}

	private static async Task<(Guid AccountId, Guid MessageId)> SeedAsync(TestDatabase database) =>
		await UsingAsync(
			database,
			async context =>
			{
				var account = new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap };
				var messageId = Guid.NewGuid();
				context.Accounts.Add(account);
				context.Messages.Add(
					new Message { Id = messageId, AccountId = account.Id, ReceivedAt = DateTimeOffset.UnixEpoch }
				);
				await context.SaveChangesAsync();
				return (account.Id, messageId);
			}
		);
}
