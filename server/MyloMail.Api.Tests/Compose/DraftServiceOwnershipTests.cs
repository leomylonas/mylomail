using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// Seventy-fourth pass: the same cross-account-ownership gap passes 71-73 closed for mailbox
/// parent ids and MutationQueue's messageId/targetMailboxId, here in
/// <see cref="DraftService.SaveAsync"/> — a client-supplied <c>SendIdentityId</c> was never
/// checked against the draft's actual account, and an existing draft's actual account was
/// never checked against a client-supplied <c>AccountId</c> either.
/// </summary>
public sealed class DraftServiceOwnershipTests
{
	[Fact]
	public async Task Saving_with_a_send_identity_from_another_account_is_rejected()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var otherAccountId = Guid.NewGuid();
		var otherIdentityId = Guid.NewGuid();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(
				new Account
				{
					Id = otherAccountId,
					DisplayName = "Other",
					ProviderType = ProviderType.Gmail,
				}
			);
			context.SendIdentities.Add(
				new SendIdentity
				{
					Id = otherIdentityId,
					AccountId = otherAccountId,
					EmailAddress = "other@example.test",
					IsDefault = true,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async scope =>
		{
			var ex = await Assert.ThrowsAsync<HubException>(
				() =>
					scope
						.GetRequiredService<DraftService>()
						.SaveAsync(
							new DraftInput(
								DraftId: null,
								AccountId: harness.Account.Id,
								SendIdentityId: otherIdentityId,
								InReplyToMessageId: null,
								To: [],
								Cc: [],
								Bcc: [],
								Subject: "Test",
								BodyHtml: "<p>Test</p>"
							)
						)
			);
			Assert.Contains(otherIdentityId.ToString(), ex.Message);
		});

		await harness.UsingAsync(async scope =>
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().Drafts.ToListAsync())
		);
	}

	[Fact]
	public async Task Saving_an_existing_draft_under_a_mismatched_account_is_rejected()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var otherAccountId = Guid.NewGuid();
		var draftId = Guid.NewGuid();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(
				new Account
				{
					Id = otherAccountId,
					DisplayName = "Other",
					ProviderType = ProviderType.Gmail,
				}
			);
			var identity = new SendIdentity
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				EmailAddress = "author@example.test",
				IsDefault = true,
			};
			context.SendIdentities.Add(identity);
			context.Drafts.Add(
				new Draft
				{
					Id = draftId,
					AccountId = harness.Account.Id,
					SendIdentityId = identity.Id,
					Subject = "My draft",
					BodyHtml = "<p>My draft</p>",
					SavedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async scope =>
		{
			var ex = await Assert.ThrowsAsync<HubException>(
				() =>
					scope
						.GetRequiredService<DraftService>()
						.SaveAsync(
							new DraftInput(
								DraftId: draftId,
								AccountId: otherAccountId,
								SendIdentityId: null,
								InReplyToMessageId: null,
								To: [],
								Cc: [],
								Bcc: [],
								Subject: "Hijacked subject",
								BodyHtml: "<p>Hijacked</p>"
							)
						)
			);
			Assert.Contains(draftId.ToString(), ex.Message);
		});

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync(d => d.Id == draftId);
			Assert.Equal("My draft", draft.Subject);
		});
	}
}
