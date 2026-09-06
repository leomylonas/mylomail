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
/// Two-hundred-and-fifty-second pass: <see cref="DraftService.AddAttachmentAsync"/> silently
/// defaulted every uploaded attachment to <c>IsInline = false</c>/<c>ContentId = null</c>, with
/// no way for a caller to say otherwise — the exact gap that let a reply/forward to a message
/// with an inline image lose that image's <c>cid:</c> binding when its bytes were copied onto
/// the new draft (the copy always looked like an ordinary attachment, never an inline one).
/// </summary>
public sealed class DraftAttachmentInlineTests
{
	private static async Task<Guid> SeedDraftAsync(SyncHarness harness)
	{
		var draftId = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
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
					Subject = "Test",
					BodyHtml = "<p>Test</p>",
					SavedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		});
		return draftId;
	}

	[Fact]
	public async Task An_uploaded_attachment_can_be_marked_inline_with_a_content_id()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var draftId = await SeedDraftAsync(harness);

		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<DraftService>()
				.AddAttachmentAsync(
					draftId,
					"logo.png",
					"image/png",
					[1, 2, 3],
					isInline: true,
					contentId: "logo@mylomail.local"
				)
		);

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Drafts.SingleAsync(d => d.Id == draftId);
			var attachment = Assert.Single(draft.Attachments);
			Assert.True(attachment.IsInline);
			Assert.Equal("logo@mylomail.local", attachment.ContentId);
		});
	}

	[Fact]
	public async Task An_uploaded_attachment_defaults_to_not_inline_with_no_content_id()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var draftId = await SeedDraftAsync(harness);

		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<DraftService>()
				.AddAttachmentAsync(draftId, "report.pdf", "application/pdf", [1, 2, 3])
		);

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Drafts.SingleAsync(d => d.Id == draftId);
			var attachment = Assert.Single(draft.Attachments);
			Assert.False(attachment.IsInline);
			Assert.Null(attachment.ContentId);
		});
	}
}
