using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Security;

namespace MyloMail.Api.Providers.Imap;

public sealed partial class ImapMailProvider
{
	/// <summary>Why the Sent copy could not be filed, if it could not. Diagnostics only.</summary>
	public string? AppendFailure => appendFailure;

	/// <summary>Forces <see cref="AppendFailure"/> without a real append failure — the append
	/// path needs a live IMAP/SMTP round trip that deliberately fails APPEND to exercise for
	/// real, so this lets a test drive <see cref="Outbox.SendExecutor"/>'s consumption of the
	/// property directly instead.</summary>
	internal void SimulateAppendFailure(string message) => appendFailure = message;

	private string? appendFailure;

	/// <summary>
	/// Sends over SMTP, then files a copy if the server will not (§15).
	/// </summary>
	/// <remarks>
	/// <para>
	/// <paramref name="stableMessageId"/> becomes the message's <c>Message-ID</c> header, not a
	/// fresh one. It was generated before the first attempt precisely so that a send whose
	/// outcome was never observed can be found in the Sent mailbox afterwards — minting a new
	/// one here would make that reconciliation impossible (§6).
	/// </para>
	/// <para>
	/// The append is a separate operation from the send and can fail on its own. That is
	/// deliberate: the message has already left, and failing the send because the copy could
	/// not be filed would invite a retry that sends it twice.
	/// </para>
	/// </remarks>
	public async Task SendAsync(
		Account account,
		Draft draft,
		string stableMessageId,
		CancellationToken ct
	)
	{
		var message = Compose(draft, stableMessageId);

		using (var smtp = new SmtpClient())
		{
			(string Fingerprint, string Issuer)? rejected = null;
			smtp.ServerCertificateValidationCallback = (_, certificate, _, sslPolicyErrors) =>
			{
				if (certificate is null)
				{
					return false;
				}

				var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
				var trusted = CertificateTrust.Validate(
					settings.CertificateTrustMode,
					settings.TrustedCertificates ?? [],
					settings.SmtpHost,
					certificate2,
					sslPolicyErrors
				);
				if (!trusted)
				{
					rejected = (CertificateTrust.Fingerprint(certificate2), certificate2.Issuer);
				}
				return trusted;
			};

			try
			{
				await smtp.ConnectAsync(
					settings.SmtpHost,
					settings.SmtpPort,
					settings.UseSsl ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None,
					ct
				);
			}
			catch (SslHandshakeException) when (rejected is { } certificateRejection)
			{
				// A definite pre-authentication rejection, not an ambiguous outcome — nothing
				// was sent, so this must not be allowed to fall into SendExecutor's generic
				// catch, which treats a thrown send as "may have happened" and reconciles
				// against the Sent mailbox rather than simply retrying (§6). The message text
				// matches the connect-path's own CertificateTrust.Problem, so item.LastError
				// reads the same way an AddAccount rejection would.
				throw new ProviderAuthenticationException(
					CertificateTrust
						.Problem(settings.SmtpHost, certificateRejection.Fingerprint, certificateRejection.Issuer)
						.Detail!
				);
			}

			// Only authenticate where the server asks for it: the local test matrix and plenty
			// of relays accept unauthenticated loopback submission, and offering credentials
			// unprompted fails against them.
			if (smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication))
			{
				await smtp.AuthenticateAsync(
					settings.SmtpUserName ?? settings.UserName,
					settings.SmtpPassword ?? settings.Password,
					ct
				);
			}

			await smtp.SendAsync(message, ct);
			await smtp.DisconnectAsync(true, ct);
		}

		if (!settings.AppendToSent)
		{
			return;
		}

		try
		{
			using var client = await ConnectAsync(ct);
			var sent = client.GetFolder(SpecialFolder.Sent);
			if (sent is not null)
			{
				await sent.OpenAsync(FolderAccess.ReadWrite, ct);
				await sent.AppendAsync(message, MessageFlags.Seen, ct);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Swallowed on purpose. The message is already sent; surfacing this as a send
			// failure would put the outbox item into a state whose recovery is a retry, and
			// the retry would deliver a second copy.
			appendFailure = ex.Message;
		}
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
		var body = new BodyBuilder
		{
			HtmlBody = draft.BodyHtml,
			TextBody = ToPlainText(draft.BodyHtml),
		};
		foreach (var attachment in draft.Attachments)
		{
			body.Attachments.Add(attachment.Filename, attachment.Content, ContentType.Parse(attachment.MimeType));
		}
		message.Body = body.ToMessageBody();

		return message;
	}

	/// <summary>
	/// A readable text alternative for an HTML body.
	/// </summary>
	/// <remarks>
	/// Crude on purpose: this is a fallback rendering, not a parse of untrusted markup. It is
	/// generated from HTML the user composed in this app, which is why a tag-stripper is
	/// sufficient here and would not be for incoming mail.
	/// </remarks>
	private static string ToPlainText(string html) =>
		string.Join(
			' ',
			System.Text.RegularExpressions.Regex
				.Replace(html, "<[^>]+>", " ")
				.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
		);
}
