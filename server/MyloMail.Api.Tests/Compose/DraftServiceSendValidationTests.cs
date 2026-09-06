using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// Two-hundred-and-fifteenth pass: Compose.tsx only disables its Send button while the raw
/// "To" text field is empty — it never guarantees that text actually parsed into an address
/// (`parseAddresses` silently drops anything without an "@"), and it never looks at Cc/Bcc at
/// all. Nothing on the server mirrored this, so <see cref="DraftService.SendAsync"/> would
/// happily queue a draft with zero recipients. The provider's own "no recipients" rejection
/// would then land in <see cref="MyloMail.Api.Outbox.SendExecutor"/>'s generic catch-all,
/// which treats a thrown send as <see cref="MyloMail.Api.Outbox.OutboxStatus.AmbiguousOutcome"/>
/// — actively wrong for a message that provably never had anywhere to go.
/// </summary>
public sealed class DraftServiceSendValidationTests
{
	[Fact]
	public async Task Sending_a_draft_with_no_recipients_at_all_is_rejected_before_queueing()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var draftId = await SeedDraftAsync(harness, to: [], cc: [], bcc: []);

		await harness.UsingAsync(async services =>
		{
			var ex = await Assert.ThrowsAsync<InvalidOperationException>(
				() => services.GetRequiredService<DraftService>().SendAsync(draftId)
			);
			Assert.Contains("recipient", ex.Message, StringComparison.OrdinalIgnoreCase);
		});

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			// Rejected before queueing: no outbox item, and the draft survives so the user can
			// go back and add a recipient.
			Assert.False(await context.OutboxItems.AnyAsync());
			Assert.True(await context.Drafts.AnyAsync(d => d.Id == draftId));
		});
	}

	[Fact]
	public async Task Sending_a_draft_with_only_a_cc_recipient_is_accepted()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var draftId = await SeedDraftAsync(
			harness,
			to: [],
			cc: [new Address(null, "cc@example.test")],
			bcc: []
		);

		var item = await harness.UsingAsync(
			services => services.GetRequiredService<DraftService>().SendAsync(draftId)
		);

		Assert.NotEqual(Guid.Empty, item.Id);
	}

	[Fact]
	public async Task Sending_a_draft_with_only_a_bcc_recipient_is_accepted()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var draftId = await SeedDraftAsync(
			harness,
			to: [],
			cc: [],
			bcc: [new Address(null, "bcc@example.test")]
		);

		var item = await harness.UsingAsync(
			services => services.GetRequiredService<DraftService>().SendAsync(draftId)
		);

		Assert.NotEqual(Guid.Empty, item.Id);
	}

	private static async Task<Guid> SeedDraftAsync(
		MutationHarness harness,
		IReadOnlyList<Address> to,
		IReadOnlyList<Address> cc,
		IReadOnlyList<Address> bcc
	) =>
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var identity = new SendIdentity
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				DisplayName = "Test",
				EmailAddress = "test@example.org",
				IsDefault = true,
			};
			var draft = new Draft
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				SendIdentityId = identity.Id,
				Subject = "Hello",
				SavedAt = DateTimeOffset.UnixEpoch,
				To = to,
				Cc = cc,
				Bcc = bcc,
			};

			context.SendIdentities.Add(identity);
			context.Drafts.Add(draft);
			await context.SaveChangesAsync();
			return draft.Id;
		});
}
