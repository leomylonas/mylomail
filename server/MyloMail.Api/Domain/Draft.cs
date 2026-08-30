namespace MyloMail.Api.Domain;

/// <summary>
/// A draft is structured authoring state, not raw MIME (§1). Unlike received and sent
/// messages, a draft is a mutable document; MIME is generated at save-to-server and send
/// time, and remote drafts are parsed into this structure.
/// </summary>
public class Draft
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public Guid SendIdentityId { get; set; }
	public Guid? InReplyToMessageId { get; set; }

	public IReadOnlyList<Address> To { get; set; } = [];
	public IReadOnlyList<Address> Cc { get; set; } = [];
	public IReadOnlyList<Address> Bcc { get; set; } = [];

	public string Subject { get; set; } = string.Empty;
	public string BodyHtml { get; set; } = string.Empty;
	public IReadOnlyList<DraftAttachment> Attachments { get; set; } = [];
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>The server-side id, once the draft has been created remotely.</summary>
	public string? ProviderDraftId { get; set; }

	/// <summary>
	/// ETag or revision the local state was based on. Required for detect-don't-merge
	/// conflict handling — without it the guarantee is unenforceable (§1, §15).
	/// </summary>
	public string? ProviderRevision { get; set; }
}

public class DraftAttachment
{
	public Guid Id { get; set; }
	public string Filename { get; set; } = string.Empty;
	public string MimeType { get; set; } = string.Empty;
	public long Size { get; set; }
	public string? ContentId { get; set; }
	public bool IsInline { get; set; }
}
