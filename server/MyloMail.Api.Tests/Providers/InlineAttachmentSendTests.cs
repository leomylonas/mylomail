using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// An inline image (a <c>cid:</c> reference embedded in <see cref="Draft.BodyHtml"/>) must
/// round-trip as a <c>multipart/related</c> linked resource with a <c>Content-Id</c> header, not
/// as a plain attachment — <see cref="MyloMail.Api.Compose.RemoteDraftMaterializer"/> sets
/// <see cref="DraftAttachment.IsInline"/>/<see cref="DraftAttachment.ContentId"/> for any
/// provider's materialised remote drafts, so this is reachable for both IMAP and Gmail sends,
/// not just Graph (which already handled it via its own typed <c>IsInline</c>/<c>ContentId</c>
/// fields).
/// </summary>
public sealed class InlineAttachmentSendTests
{
	private static Draft DraftWithInlineImage() =>
		new()
		{
			FromAddress = "me@example.com",
			To = [new Address(null, "you@example.com")],
			Subject = "Inline image",
			BodyHtml = "<p>See below</p><img src=\"cid:logo@mylomail.local\">",
			Attachments =
			[
				new DraftAttachment
				{
					Filename = "logo.png",
					MimeType = "image/png",
					ContentId = "logo@mylomail.local",
					IsInline = true,
					Content = [1, 2, 3],
				},
			],
		};

	[Fact]
	public void Imap_send_keeps_an_inline_image_out_of_the_visible_attachment_list()
	{
		var message = ImapMailProvider.Compose(DraftWithInlineImage(), "<stable@mylomail.local>");

		Assert.Empty(message.Attachments);
		var resource = Assert.Single(message.BodyParts, part => part.ContentId == "logo@mylomail.local");
		Assert.True(resource.ContentDisposition?.Disposition == "inline");
	}

	[Fact]
	public void Gmail_send_keeps_an_inline_image_out_of_the_visible_attachment_list()
	{
		var message = GmailMailProvider.Compose(DraftWithInlineImage(), "<stable@mylomail.local>");

		Assert.Empty(message.Attachments);
		var resource = Assert.Single(message.BodyParts, part => part.ContentId == "logo@mylomail.local");
		Assert.True(resource.ContentDisposition?.Disposition == "inline");
	}

	[Fact]
	public void Imap_send_still_treats_a_non_inline_attachment_as_a_plain_attachment()
	{
		var draft = DraftWithInlineImage();
		draft.Attachments =
		[
			new DraftAttachment
			{
				Filename = "report.pdf",
				MimeType = "application/pdf",
				IsInline = false,
				Content = [4, 5, 6],
			},
		];
		draft.BodyHtml = "<p>See attached</p>";

		var message = ImapMailProvider.Compose(draft, "<stable@mylomail.local>");

		var attachment = Assert.Single(message.Attachments);
		Assert.Null(attachment.ContentId);
	}
}
