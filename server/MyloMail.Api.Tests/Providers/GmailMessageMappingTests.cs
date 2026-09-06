using Google.Apis.Gmail.v1.Data;
using MyloMail.Api.Providers.Gmail;
using Xunit;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// Gmail's Full-format response hands back every RFC 5322 header verbatim in
/// <c>Payload.Headers</c> — unlike IMAP's own structured ENVELOPE and Graph's typed
/// from/toRecipients/etc. fields — but parsing them into <see cref="MessageDto"/>'s address
/// fields was missed entirely. Without it, every Gmail message would persist with empty
/// From/To/Cc/Bcc/ReplyTo (<c>MessageIngestor.cs</c>: <c>message.From = dto.From</c>, and
/// likewise the others): a blank sender/recipients in the message list, "from:"/"to:"/"cc:"
/// search never matching a single Gmail message, and reply routing silently falling through to
/// an empty From.
/// </summary>
public sealed class GmailMessageMappingTests
{
	[Fact]
	public void From_to_cc_bcc_and_reply_to_are_parsed_from_the_raw_headers()
	{
		var message = MessageWithHeaders(
			("From", "Alice <alice@example.com>"),
			("To", "Bob <bob@example.com>, carol@example.com"),
			("Cc", "Dave <dave@example.com>"),
			("Bcc", "Erin <erin@example.com>"),
			("Reply-To", "Support <support@example.com>")
		);

		var dto = GmailMailProvider.ToDto(message);

		Assert.Equal([("Alice", "alice@example.com")], Names(dto.From));
		Assert.Equal(
			[("Bob", "bob@example.com"), ("", "carol@example.com")],
			Names(dto.To)
		);
		Assert.Equal([("Dave", "dave@example.com")], Names(dto.Cc));
		Assert.Equal([("Erin", "erin@example.com")], Names(dto.Bcc));
		Assert.Equal([("Support", "support@example.com")], Names(dto.ReplyToAddresses));
	}

	[Fact]
	public void A_missing_address_header_yields_an_empty_list_not_a_crash()
	{
		var message = MessageWithHeaders(("Subject", "No senders here"));

		var dto = GmailMailProvider.ToDto(message);

		Assert.Empty(dto.From);
		Assert.Empty(dto.To);
		Assert.Empty(dto.Cc);
		Assert.Empty(dto.Bcc);
		Assert.Empty(dto.ReplyToAddresses);
	}

	private static IEnumerable<(string? Name, string Email)> Names(
		IReadOnlyList<MyloMail.Api.Domain.Address> addresses
	) => addresses.Select(a => (a.Name, a.Email));

	private static GmailMessage MessageWithHeaders(params (string Name, string Value)[] headers) =>
		new()
		{
			Id = "msg-1",
			Payload = new MessagePart
			{
				Headers = [.. headers.Select(h => new MessagePartHeader { Name = h.Name, Value = h.Value })],
			},
		};
}
