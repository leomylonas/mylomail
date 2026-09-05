using Google.Apis.Gmail.v1;
using MimeKit;
using MyloMail.Api.Domain;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// Sending is a single <c>messages.send</c> call carrying the whole RFC 5322 message, base64url
/// encoded (§1) — unlike Graph, Gmail has no separate small-attachment/chunked-upload split,
/// since <see cref="GmailMailProvider.GetAttachmentConstraintsAsync"/> already caps a message at
/// 35MB total and the API accepts that in one request.
/// </summary>
public sealed partial class GmailMailProvider
{
	public async Task SendAsync(
		Account account,
		Draft draft,
		string stableMessageId,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var message = new GmailMessage { Raw = ToBase64Url(Compose(draft, stableMessageId)) };
		await service.Users.Messages.Send(message, UserId).ExecuteThrottleAwareAsync(ct);
	}

	/// <summary>Builds the MIME message from the draft's structured fields (§1).</summary>
	private static MimeMessage Compose(Draft draft, string stableMessageId)
	{
		var message = new MimeMessage { MessageId = stableMessageId.Trim('<', '>') };

		message.From.Add(MailboxAddress.Parse(draft.FromAddress));
		foreach (var address in draft.To)
		{
			message.To.Add(new MailboxAddress(address.Name, address.Email));
		}

		foreach (var address in draft.Cc)
		{
			message.Cc.Add(new MailboxAddress(address.Name, address.Email));
		}

		foreach (var address in draft.Bcc)
		{
			message.Bcc.Add(new MailboxAddress(address.Name, address.Email));
		}

		message.Subject = draft.Subject;
		message.Date = DateTimeOffset.UtcNow;

		if (draft.InReplyToHeader is string inReplyTo)
		{
			// Both, because a reply that sets only In-Reply-To breaks threading in clients that
			// follow References, and vice versa (RFC 5322).
			message.InReplyTo = inReplyTo;
			message.References.Add(inReplyTo);
		}

		// A plain-text alternative alongside the HTML, because a message with no text part is
		// unreadable in clients that prefer one and reads poorly in notification previews.
		var body = new BodyBuilder { HtmlBody = draft.BodyHtml, TextBody = ToPlainText(draft.BodyHtml) };
		foreach (var attachment in draft.Attachments)
		{
			body.Attachments.Add(attachment.Filename, attachment.Content, ContentType.Parse(attachment.MimeType));
		}
		message.Body = body.ToMessageBody();

		return message;
	}

	/// <summary>A readable text alternative for an HTML body — crude on purpose, since it
	/// converts markup this app itself composed rather than parsing untrusted input.</summary>
	private static string ToPlainText(string html) =>
		string.Join(
			' ',
			System.Text.RegularExpressions.Regex
				.Replace(html, "<[^>]+>", " ")
				.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
		);

	private static string ToBase64Url(MimeMessage message)
	{
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return Convert.ToBase64String(stream.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
	}
}
