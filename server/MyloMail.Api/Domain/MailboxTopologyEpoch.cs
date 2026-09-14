namespace MyloMail.Api.Domain;

/// <summary>
/// Monotonic generation for a provider mailbox identity. Unlike <see cref="Mailbox"/>, this
/// survives local mailbox deletion so an old in-flight page cannot mutate a recreated mailbox.
/// </summary>
public sealed class MailboxTopologyEpoch
{
	public Guid AccountId { get; set; }
	public string ProviderMailboxId { get; set; } = string.Empty;
	public int Generation { get; set; }
}
