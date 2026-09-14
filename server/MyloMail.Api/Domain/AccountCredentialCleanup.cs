namespace MyloMail.Api.Domain;

/// <summary>
/// Durable intent to erase every credential namespace that belonged to a removed account.
/// It intentionally has no account foreign key: it must survive the account's deletion until
/// the external credential store has acknowledged every idempotent delete.
/// </summary>
public sealed class AccountCredentialCleanup
{
	public Guid AccountId { get; set; }
}
