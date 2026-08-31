using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Server-side draft storage, which this build does not use.
/// </summary>
/// <remarks>
/// Drafts are held locally and composed into MIME at send time (§1). Pushing them to the
/// server's Drafts folder is a separate feature — it is what makes a draft started here
/// appear on another device — and until it exists these throw rather than returning a
/// plausible empty result, so a caller finds out at the call site.
/// </remarks>
public sealed partial class ImapMailProvider
{
	private const string NotThinStage =
		"Not implemented at stage B, which is deliberately thin. See docs/architecture.md §16.";

	public Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct) =>
		throw new NotSupportedException(NotThinStage);
}
