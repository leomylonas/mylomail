using System.ComponentModel.DataAnnotations.Schema;

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

	/// <summary>
	/// The address this draft sends from, resolved at send time from its
	/// <see cref="SendIdentityId"/>.
	/// </summary>
	/// <remarks>
	/// Deliberately not persisted. The identity is the stored fact; its address is derived,
	/// and storing both would be two answers to which address a draft sends from — the same
	/// duplication <see cref="Account"/> avoids by having no address column at all (§1). The
	/// provider needs the resolved value, so the caller fills it in before dispatch.
	/// </remarks>
	[NotMapped]
	public string FromAddress { get; set; } = string.Empty;

	/// <summary>
	/// The RFC 5322 <c>Message-ID</c> of the message being replied to, resolved at send time
	/// from <see cref="InReplyToMessageId"/>. Not persisted, for the same reason.
	/// </summary>
	[NotMapped]
	public string? InReplyToHeader { get; set; }
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
