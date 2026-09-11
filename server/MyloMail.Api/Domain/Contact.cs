namespace MyloMail.Api.Domain;

/// <summary>Canonical, account-local contact identity. Provider identifiers are metadata only.</summary>
public sealed class Contact
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public string DisplayName { get; set; } = string.Empty;
	public string? ProviderContactId { get; set; }
	public string? ProviderContainerId { get; set; }
	public string? ProviderRevision { get; set; }
	public DateTimeOffset? ProviderMissingSince { get; set; }
	public bool SyncConflict { get; set; }
	public List<ContactAddress> Addresses { get; set; } = [];
}

/// <summary>
/// Durable local contact intent. A dispatched row proves only that the provider may have
/// observed the operation; it is deliberately retained until outcome reconciliation settles it.
/// </summary>
public sealed class ContactOperation
{
	public long Sequence { get; set; }
	public Guid Id { get; set; }
	public Guid ContactId { get; set; }
	public ContactOperationKind Kind { get; set; }
	public ContactOperationState State { get; set; }
	public string DisplayName { get; set; } = string.Empty;
	public string EmailsJson { get; set; } = "[]";
	public string? ExpectedRevision { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? DispatchedAt { get; set; }
	public DateTimeOffset? SettledAt { get; set; }
}

public enum ContactOperationKind
{
	Create,
	Update,
	Delete,
}

public enum ContactOperationState
{
	Pending,
	Dispatched,
	Ambiguous,
	Completed,
	Conflict,
	NotApplied,
	Rejected,
}

/// <summary>One normalized, unique email address belonging to a contact.</summary>
public sealed class ContactAddress
{
	public Guid Id { get; set; }
	public Guid ContactId { get; set; }
	public string Email { get; set; } = string.Empty;
	public string NormalizedEmail { get; set; } = string.Empty;
}

/// <summary>An address learned from account message traffic; never overwrites a contact.</summary>
public sealed class ContactSuggestion
{
	public Guid AccountId { get; set; }
	public string Email { get; set; } = string.Empty;
	public string NormalizedEmail { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
}
