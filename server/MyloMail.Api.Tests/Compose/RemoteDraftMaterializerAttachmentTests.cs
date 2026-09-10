using MimeKit;
using MyloMail.Api.Compose;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// <see cref="RemoteDraftMaterializer.Attachments"/> used to walk only
/// <see cref="MimeMessage.Attachments"/>, which MimeKit documents as enumerating only parts whose
/// Content-Disposition is literally "attachment" — an inline image (Content-Disposition: inline,
/// or no disposition at all) referenced from the body by cid: was invisible to it entirely, not
/// just misclassified, so a remotely materialised draft with an inline image silently lost that
/// image's bytes from <c>DraftAttachment.Content</c>.
/// </summary>
public sealed class RemoteDraftMaterializerAttachmentTests
{
	[Fact]
	public void An_inline_image_with_no_content_disposition_is_captured()
	{
		var mime = new MimeMessage();
		mime.From.Add(new MailboxAddress("Sender", "sender@example.com"));
		mime.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
		mime.Subject = "Inline image, no disposition header";

		var image = new MimePart("image", "png")
		{
			Content = new MimeContent(new MemoryStream([1, 2, 3, 4])),
			ContentTransferEncoding = ContentEncoding.Base64,
			ContentId = "logo@mylomail.local",
			// Deliberately no ContentDisposition at all — some real-world clients omit it for an
			// image referenced purely by cid:, relying on the Content-Id alone.
		};

		var body = new TextPart("html") { Text = "<p>See <img src=\"cid:logo@mylomail.local\"></p>" };
		mime.Body = new Multipart("related") { body, image };

		var attachments = RemoteDraftMaterializer.Attachments(mime).ToList();

		var inline = Assert.Single(attachments);
		Assert.True(inline.IsInline);
		Assert.Equal("logo@mylomail.local", inline.ContentId);
		Assert.Equal([1, 2, 3, 4], inline.Content);
	}

	[Fact]
	public void An_inline_image_with_an_explicit_inline_disposition_is_captured()
	{
		var mime = new MimeMessage();
		mime.From.Add(new MailboxAddress("Sender", "sender@example.com"));
		mime.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
		mime.Subject = "Inline image, explicit disposition";

		var image = new MimePart("image", "png")
		{
			Content = new MimeContent(new MemoryStream([5, 6, 7, 8])),
			ContentTransferEncoding = ContentEncoding.Base64,
			ContentId = "logo2@mylomail.local",
			ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
		};

		var body = new TextPart("html") { Text = "<p>See <img src=\"cid:logo2@mylomail.local\"></p>" };
		mime.Body = new Multipart("related") { body, image };

		var attachments = RemoteDraftMaterializer.Attachments(mime).ToList();

		var inline = Assert.Single(attachments);
		Assert.True(inline.IsInline);
		Assert.Equal("logo2@mylomail.local", inline.ContentId);
	}

	[Fact]
	public void A_real_attachment_is_still_captured_and_not_marked_inline()
	{
		var mime = new MimeMessage();
		mime.From.Add(new MailboxAddress("Sender", "sender@example.com"));
		mime.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
		mime.Subject = "Plain attachment";

		var pdf = new MimePart("application", "pdf")
		{
			Content = new MimeContent(new MemoryStream([9, 9, 9])),
			ContentTransferEncoding = ContentEncoding.Base64,
			FileName = "report.pdf",
			ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
		};

		var body = new TextPart("html") { Text = "<p>See attached.</p>" };
		mime.Body = new Multipart("mixed") { body, pdf };

		var attachments = RemoteDraftMaterializer.Attachments(mime).ToList();

		var attachment = Assert.Single(attachments);
		Assert.False(attachment.IsInline);
		Assert.Equal("report.pdf", attachment.Filename);
	}

	[Fact]
	public void A_remote_draft_with_too_many_attachment_parts_is_rejected_before_materialization()
	{
		var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "Body" } };
		for (var i = 0; i < 513; i++)
		{
			multipart.Add(
				new MimePart("application", "octet-stream")
				{
					Content = new MimeContent(new MemoryStream([1])),
					ContentTransferEncoding = ContentEncoding.Base64,
					FileName = $"part-{i}",
					ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
				}
			);
		}
		var mime = new MimeMessage { Body = multipart };

		Assert.Throws<InvalidOperationException>(() => RemoteDraftMaterializer.Attachments(mime).ToList());
	}
}
