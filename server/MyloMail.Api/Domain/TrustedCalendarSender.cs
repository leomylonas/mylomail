using Tapper;

namespace MyloMail.Api.Domain;

/// <summary>
/// A sender the user has decided to believe about calendar invitations and replies, whether or not
/// their domain publishes the DKIM and DMARC that would let the app check it (a small business or
/// personal domain often does not).
/// </summary>
public class TrustedCalendarSender
{
	public Guid Id { get; set; }
	public TrustedCalendarSenderScope Scope { get; set; }

	/// <summary>Lower-case: a full address for <see cref="TrustedCalendarSenderScope.Sender"/>, a bare domain for <see cref="TrustedCalendarSenderScope.Domain"/>.</summary>
	public required string Value { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}

[TranspilationSource]
public enum TrustedCalendarSenderScope
{
	Sender,
	Domain,
}
