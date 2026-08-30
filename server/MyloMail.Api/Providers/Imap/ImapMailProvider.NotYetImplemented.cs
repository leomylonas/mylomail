using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Surface the thin stage does not reach (§16, stage B: authentication, topology, one read
/// path, one mutation). These throw rather than returning a plausible empty result, so a
/// caller finds out at the call site instead of silently doing nothing.
/// </summary>
public sealed partial class ImapMailProvider
{
	private const string NotThinStage =
		"Not implemented at stage B, which is deliberately thin. See docs/architecture.md §16.";

	public Task SendAsync(
		Account account,
		Draft draft,
		string stableMessageId,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct) =>
		throw new NotSupportedException(NotThinStage);

	public Task<MailboxDto> CreateMailboxAsync(
		Account account,
		string name,
		Mailbox? parent,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public Task RenameMailboxAsync(
		Account account,
		Mailbox mailbox,
		string newName,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public Task MoveMailboxAsync(
		Account account,
		Mailbox mailbox,
		Mailbox? newParent,
		CancellationToken ct
	) => throw new NotSupportedException(NotThinStage);

	public Task DeleteMailboxAsync(Account account, Mailbox mailbox, CancellationToken ct) =>
		throw new NotSupportedException(NotThinStage);
}
