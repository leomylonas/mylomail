using MailKit;
using MimeKit;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// IMAP's ENVELOPE fetch alone carries no attachment information — <see cref="MessageDto"/>'s
/// <c>HasNonInlineAttachments</c> defaulted to <see langword="false"/> for every IMAP message
/// forever, since nothing re-announces the message list once <c>ContentAcquisition</c> later
/// corrects it from the real MIME. <c>BODYSTRUCTURE</c> reveals attachment shape cheaply, without
/// a full-body fetch — <see cref="ImapMailProvider.HasNonInlineAttachment"/> walks that tree.
/// </summary>
public sealed class ImapMessageMappingTests
{
	[Fact]
	public void A_non_inline_attachment_part_is_detected()
	{
		var text = new BodyPartBasic(new ContentType("text", "plain"), "1");
		var pdf = new BodyPartBasic(new ContentType("application", "pdf"), "2")
		{
			ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
		};
		var parts = new BodyPartCollection { text, pdf };
		var mixed = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
		foreach (var part in parts)
		{
			mixed.BodyParts.Add(part);
		}

		Assert.True(ImapMailProvider.HasNonInlineAttachment(mixed));
	}

	[Fact]
	public void An_inline_image_part_is_not_counted_as_an_attachment()
	{
		var html = new BodyPartBasic(new ContentType("text", "html"), "1");
		var image = new BodyPartBasic(new ContentType("image", "png"), "2")
		{
			ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
		};
		var related = new BodyPartMultipart(new ContentType("multipart", "related"), "");
		related.BodyParts.Add(html);
		related.BodyParts.Add(image);

		Assert.False(ImapMailProvider.HasNonInlineAttachment(related));
	}

	[Fact]
	public void References_preserve_each_RFC_message_id_boundary()
	{
		var references = new MessageIdList
		{
			"root@example.test",
			"parent@example.test",
		};

		Assert.Equal(
			"<root@example.test> <parent@example.test>",
			ImapMailProvider.SerializeReferences(references)
		);
	}

	[Fact]
	public void A_null_body_structure_is_not_an_attachment()
	{
		Assert.False(ImapMailProvider.HasNonInlineAttachment(null));
	}
}
