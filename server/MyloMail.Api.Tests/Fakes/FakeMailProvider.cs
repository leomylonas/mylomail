using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IMailProvider"/>, built alongside the real providers rather than
/// as an afterthought (§11). It backs unit tests of the orchestrator, job logic and startup
/// reconciliation without any real provider dependency.
/// </summary>
/// <remarks>
/// It is constructed with an explicit <see cref="ProviderCapabilities"/> so it can take the
/// shape of any provider, including each IMAP capability tier. That is what lets the shared
/// conformance suite run against every shape before a single real provider exists — the
/// suite is then known to discriminate, rather than being first exercised by the code it is
/// meant to judge.
/// <para>
/// It models the behaviour the invariants depend on: a move mints a new occurrence id, as an
/// IMAP move does; batch results are per item; an unusable cursor raises
/// <see cref="ProviderCursorInvalidException"/>.
/// </para>
/// </remarks>
public sealed class FakeMailProvider : IMailProvider
{
	private const int PageSize = 50;

	private readonly Dictionary<string, FakeMailbox> mailboxes = [];
	private readonly HashSet<string> omitted = [];
	private Exception? sendFailure;
	private Exception? fetchRawMessageFailure;
	private Exception? draftPushFailure;
	private Exception? mailboxOperationFailure;
	private int draftPushSuccessesBeforeFailure;
	private string? authFailure;
	private MutationProblemDetails? authFailureProblem;
	private long occurrenceSequence;

	/// <summary>Bumped whenever the fake server invalidates outstanding cursors.</summary>
	private int cursorGeneration = 1;

	public FakeMailProvider(ProviderCapabilities capabilities)
	{
		Capabilities = capabilities;
	}

	public ProviderType Type => Capabilities.Type;

	public ProviderCapabilities Capabilities { get; }

	public FakeMailbox AddMailbox(string providerMailboxId, SpecialUse specialUse = SpecialUse.None)
	{
		var mailbox = new FakeMailbox(providerMailboxId, specialUse);
		mailboxes[providerMailboxId] = mailbox;
		return mailbox;
	}

	/// <summary>
	/// Makes the fake omit one occurrence from its batch results, as a provider that reports
	/// on fewer items than were submitted. §6 treats an unreported item as unresolved rather
	/// than successful, and partial batch results are the normal case, not an edge one.
	/// </summary>
	public void OmitFromBatchResults(string providerOccurrenceId) => omitted.Add(providerOccurrenceId);

	/// <summary>Makes the next send throw, so a rejection path can be exercised.</summary>
	public void FailSendWith(Exception failure) => sendFailure = failure;

	/// <summary>Makes the next raw-message fetch throw, so a content-fetch failure path can be exercised.</summary>
	public void FailFetchRawMessageWith(Exception failure) => fetchRawMessageFailure = failure;

	/// <summary>Makes the next folder create/rename/move/delete throw, so a provider rejection
	/// (a duplicate name, a namespace it won't accept) can be exercised.</summary>
	public void FailMailboxOperationWith(Exception failure) => mailboxOperationFailure = failure;

	/// <summary>
	/// Makes a later draft push throw after minting a provider id (a real remote draft was
	/// created — the failure is the process crashing before that news gets home, not the
	/// provider call itself failing). <paramref name="successesBeforeFailure"/> earlier pushes
	/// within the same batch succeed normally first, so a caller can assert those earlier
	/// successes are not lost when a later one in the same batch fails.
	/// </summary>
	public void FailDraftPushWith(Exception failure, int successesBeforeFailure = 0)
	{
		draftPushFailure = failure;
		draftPushSuccessesBeforeFailure = successesBeforeFailure;
	}

	/// <summary>Removes a mailbox behind the client's back, as another client would.</summary>
	public void RemoveMailbox(string providerMailboxId) => mailboxes.Remove(providerMailboxId);

	/// <summary>
	/// Removes an occurrence behind the client's back, as another client or another device
	/// would. A mutation addressing it then fails on the server's terms rather than through a
	/// test-only failure switch.
	/// </summary>
	public void RemoveMessage(string providerOccurrenceId)
	{
		foreach (var mailbox in mailboxes.Values)
		{
			mailbox.Messages.Remove(providerOccurrenceId);
		}
	}

	/// <summary>Simulates a server-side event that makes every outstanding cursor unusable.</summary>
	public void InvalidateCursors() => cursorGeneration++;

	/// <summary>Places a message in a mailbox and returns the occurrence id minted for it.</summary>
	public string SeedMessage(string providerMailboxId, Guid messageId, DateTimeOffset receivedAt)
	{
		var mailbox = Require(providerMailboxId);
		var occurrenceId = NextOccurrenceId();
		mailbox.Messages[occurrenceId] = new FakeMessage(messageId, receivedAt);
		return occurrenceId;
	}

	public ProviderCursorState CurrentCursor() =>
		Type switch
		{
			ProviderType.Gmail => new GmailHistoryCursor(cursorGeneration.ToString()),
			ProviderType.Microsoft365 => new GraphDeltaCursor($"delta-{cursorGeneration}"),
			_ => new ImapUidCursor((uint)cursorGeneration, (uint)occurrenceSequence, null, null),
		};

	private string NextOccurrenceId() => $"occ-{Interlocked.Increment(ref occurrenceSequence)}";

	private FakeMailbox Require(string providerMailboxId) =>
		mailboxes.TryGetValue(providerMailboxId, out var mailbox)
			? mailbox
			: throw new InvalidOperationException($"unknown mailbox '{providerMailboxId}'");

	private static string ProviderIdOf(Mailbox mailbox) =>
		mailbox.ProviderMailboxId
		?? throw new InvalidOperationException(
			"a synthesised mailbox has no provider object and cannot be addressed"
		);

	/// <summary>Makes authentication report a rejection, as a wrong password would.</summary>
	public void FailAuthentication(string reason) => authFailure = reason;

	/// <summary>Makes authentication report a specific problem — e.g. a certificate rejection with fingerprint/hostname Extensions.</summary>
	public void FailAuthentication(MutationProblemDetails problem) => authFailureProblem = problem;

	public Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct) =>
		Task.FromResult(
			authFailureProblem is { } problem
				? new AuthResult(false, AuthState.Error, problem)
				: authFailure is string reason
					? new AuthResult(
						false,
						AuthState.NeedsReauth,
						new MutationProblemDetails { Title = "Authentication failed", Detail = reason, Category = ErrorCategory.Auth }
					)
					: new AuthResult(true, AuthState.Connected, null)
		);

	public Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(Account account, CancellationToken ct)
	{
		IReadOnlyList<MailboxDto> result = [.. mailboxes.Values.Select(Describe)];
		return Task.FromResult(result);
	}

	private MailboxDto Describe(FakeMailbox mailbox) =>
		new()
		{
			ProviderMailboxId = mailbox.ProviderMailboxId,
			Name = mailbox.ProviderMailboxId,
			SpecialUse = mailbox.SpecialUse,
			IsSubscribed = true,
			TotalCount = Capabilities.ReportsMailboxCounts ? mailbox.Messages.Count : null,
			UnreadCount = Capabilities.ReportsMailboxCounts
				? mailbox.Messages.Values.Count(x => !x.IsRead)
				: null,
		};

	public Task<int> EstimateMailboxCountAsync(Account account, Mailbox mailbox, CancellationToken ct) =>
		Task.FromResult(Require(ProviderIdOf(mailbox)).Messages.Count);

	public Task<InitialSyncPage> InitialSyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	)
	{
		var source = Require(ProviderIdOf(mailbox));
		var ordered = source
			.Messages.OrderByDescending(kv => kv.Value.ReceivedAt)
			.Skip(int.TryParse(resumeToken, out var offset) ? offset : 0)
			.ToList();

		var page = ordered.Take(pageSize).ToList();
		var consumed = (int.TryParse(resumeToken, out var previous) ? previous : 0) + page.Count;
		var hasMore = consumed < source.Messages.Count;

		return Task.FromResult(
			new InitialSyncPage(
				[.. page.Select(kv => ToDto(source.ProviderMailboxId, kv.Key, kv.Value))],
				hasMore ? consumed.ToString() : null,
				hasMore,
				source.Messages.Count
			)
		);
	}

	public Task<SyncResult> SyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		ProviderCursorState? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		if (cursor is not null && CursorGenerationOf(cursor) != cursorGeneration)
		{
			throw new ProviderCursorInvalidException(
				$"cursor for '{ProviderIdOf(mailbox)}' predates a server-side invalidation"
			);
		}

		var source = Require(ProviderIdOf(mailbox));
		var messages = source.Messages.Skip(int.TryParse(continuation, out var offset) ? offset : 0);
		var page = messages.Take(PageSize).ToList();
		var consumed = (int.TryParse(continuation, out var seen) ? seen : 0) + page.Count;
		var more = consumed < source.Messages.Count;

		return Task.FromResult(
			new SyncResult(
				// Null while the walk is incomplete, unless this provider's cursor is monotone
				// over what has already been returned.
				more && !Capabilities.AdvancesCursorMidWalk ? null : CurrentCursor(),
				more ? consumed.ToString() : null,
				[.. page.Select(kv => ToDto(source.ProviderMailboxId, kv.Key, kv.Value))],
				[],
				[.. source.Removed.Select(id => new OccurrenceRemoval(source.ProviderMailboxId, id))]
			)
		);
	}

	public Task<MailboxIntegritySnapshot> GetMailboxIntegritySnapshotAsync(
		Account account,
		Mailbox mailbox,
		IReadOnlyList<MessageOccurrenceRef> knownOccurrences,
		CancellationToken ct
	)
	{
		var source = Require(ProviderIdOf(mailbox));
		var flags = Capabilities.SupportsIncrementalFlagChanges
			? []
			: source.Messages.Select(pair => new OccurrenceFlagChange(
				source.ProviderMailboxId,
				pair.Key,
				pair.Value.IsRead,
				pair.Value.IsFlagged
			)).ToList();
		return Task.FromResult(new MailboxIntegritySnapshot(source.Messages.Keys.ToHashSet(), flags));
	}

	private static int CursorGenerationOf(ProviderCursorState cursor) =>
		cursor switch
		{
			GmailHistoryCursor gmail => int.Parse(gmail.HistoryId),
			GraphDeltaCursor graph => int.Parse(graph.DeltaLink.Split('-')[^1]),
			ImapUidCursor imap => (int)imap.UidValidity,
			_ => throw new InvalidOperationException($"unhandled cursor kind '{cursor.Kind}'"),
		};

	public Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct
	)
	{
		if (fetchRawMessageFailure is Exception failure)
		{
			fetchRawMessageFailure = null;
			throw failure;
		}

		var found = Locate(occurrence.ProviderOccurrenceId);
		return found is null
			? throw new InvalidOperationException("no such occurrence")
			: Task.FromResult(new RawMessageResult(found.Value.Message.RawBytes));
	}

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(
		Account account,
		CancellationToken ct
	) => Task.FromResult(new AttachmentConstraints(null, null, null, IsUnknown: true));

	public Task<BatchResult> SetFlagsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	) =>
		Task.FromResult(
			PerItem(
				refs,
				found =>
				{
					// Null means leave unchanged — never clobber a flag the caller did not set.
					if (update.IsRead is bool read)
					{
						found.Message.IsRead = read;
					}

					if (update.IsFlagged is bool flagged)
					{
						found.Message.IsFlagged = flagged;
					}

					return [];
				}
			)
		);

	public Task<BatchResult> MoveMessagesAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		Mailbox target,
		CancellationToken ct
	)
	{
		var destination = Require(ProviderIdOf(target));

		return Task.FromResult(
			PerItem(
				refs,
				found =>
				{
					found.Mailbox.Messages.Remove(found.OccurrenceId);
					found.Mailbox.Removed.Add(found.OccurrenceId);

					// A move mints a new occurrence id, exactly as an IMAP move changes the
					// UID. The id the caller held is dead from here.
					var newOccurrenceId = NextOccurrenceId();
					destination.Messages[newOccurrenceId] = found.Message;

					return
					[
						new OccurrenceChange(found.Reference.MailboxId, null, Removed: true),
						// A server that cannot report the destination id returns nothing here,
						// and the caller learns that from the absent id itself.
						new OccurrenceChange(
							target.Id,
							Capabilities.ReportsDestinationIdOnMove ? newOccurrenceId : null,
							Removed: false
						),
					];
				}
			)
		);
	}

	public Task<BatchResult> RemoveFromMailboxAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) =>
		Task.FromResult(
			PerItem(
				refs,
				found =>
				{
					found.Mailbox.Messages.Remove(found.OccurrenceId);
					found.Mailbox.Removed.Add(found.OccurrenceId);
					return [new OccurrenceChange(found.Reference.MailboxId, null, Removed: true)];
				}
			)
		);

	public Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => RemoveFromMailboxAsync(account, refs, ct);

	public Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => RemoveFromMailboxAsync(account, refs, ct);

	/// <summary>
	/// Applies <paramref name="apply"/> to each reference independently, so one unresolvable
	/// item never discards the outcome of the rest.
	/// </summary>
	private BatchResult PerItem(
		IReadOnlyList<MessageOccurrenceRef> refs,
		Func<Located, IReadOnlyList<OccurrenceChange>> apply
	)
	{
		var items = new List<BatchItemResult>(refs.Count);

		foreach (var reference in refs)
		{
			if (omitted.Contains(reference.ProviderOccurrenceId))
			{
				continue;
			}

			var found = Locate(reference.ProviderOccurrenceId);
			if (found is null)
			{
				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						Succeeded: false,
						new MutationProblemDetails
						{
							Title = "Occurrence not found",
							Detail = "The message is no longer present in that mailbox on the server.",
							Category = ErrorCategory.ProviderRejected,
							ProviderCode = "NOTFOUND",
						},
						[]
					)
				);
				continue;
			}

			var located = found.Value with { Reference = reference };
			items.Add(
				new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, apply(located))
			);
		}

		return new BatchResult(items);
	}

	private Located? Locate(string providerOccurrenceId)
	{
		foreach (var mailbox in mailboxes.Values)
		{
			if (mailbox.Messages.TryGetValue(providerOccurrenceId, out var message))
			{
				return new Located(mailbox, providerOccurrenceId, message, default!);
			}
		}

		return null;
	}

	private MessageDto ToDto(string providerMailboxId, string occurrenceId, FakeMessage message) =>
		new()
		{
			ProviderStableId = Type == ProviderType.Imap ? null : $"message-{message.MessageId}",
			ProviderRevision = occurrenceId,
			Occurrences = [new MessageOccurrenceDto(providerMailboxId, occurrenceId)],
			MessageIdHeader = message.MessageIdHeader,
			ReceivedAt = message.ReceivedAt,
			IsRead = message.IsRead,
			IsFlagged = message.IsFlagged,
			Subject = message.Subject,
		};

	/// <summary>What the last <see cref="SendAsync"/> call was given, for tests that assert on send content.</summary>
	public Draft? LastSentDraft { get; private set; }

	public Task SendAsync(Account account, Draft draft, string stableMessageId, CancellationToken ct)
	{
		if (sendFailure is Exception failure)
		{
			sendFailure = null;
			throw failure;
		}

		LastSentDraft = draft;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Every provider id this fake has handed out for a draft push, in call order — a real
	/// provider has no way to tell "update" from "create" once <see cref="Draft.ProviderDraftId"/>
	/// is lost, so it always mints a fresh id, exactly like this fake does.
	/// </summary>
	public List<string> DraftProviderIdsIssued { get; } = [];

	public Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		var providerId = NextOccurrenceId();
		DraftProviderIdsIssued.Add(providerId);
		if (draftPushFailure is Exception failure)
		{
			if (draftPushSuccessesBeforeFailure > 0)
			{
				draftPushSuccessesBeforeFailure--;
			}
			else
			{
				draftPushFailure = null;
				throw failure;
			}
		}
		return Task.FromResult(new DraftResult(providerId, "rev-1"));
	}

	public Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct) =>
		Task.CompletedTask;

	public Task<MailboxDto> CreateMailboxAsync(
		Account account,
		string name,
		Mailbox? parent,
		CancellationToken ct
	)
	{
		if (mailboxOperationFailure is { } createFailure)
		{
			mailboxOperationFailure = null;
			throw createFailure;
		}

		var providerId = parent is null ? name : $"{ProviderIdOf(parent)}/{name}";
		AddMailbox(providerId);
		return Task.FromResult(
			new MailboxDto
			{
				ProviderMailboxId = providerId,
				Name = name,
				ParentProviderMailboxId = parent is null ? null : ProviderIdOf(parent),
			}
		);
	}

	public Task<MailboxDto> RenameMailboxAsync(
		Account account,
		Mailbox mailbox,
		string newName,
		CancellationToken ct
	)
	{
		if (mailboxOperationFailure is { } renameFailure)
		{
			mailboxOperationFailure = null;
			throw renameFailure;
		}

		var existing = Require(ProviderIdOf(mailbox));
		mailboxes.Remove(existing.ProviderMailboxId);
		var renamed = existing with { ProviderMailboxId = newName };
		mailboxes[newName] = renamed;
		return Task.FromResult(Describe(renamed));
	}

	public Task<MailboxDto> MoveMailboxAsync(
		Account account,
		Mailbox mailbox,
		Mailbox? newParent,
		CancellationToken ct
	) => Task.FromResult(Describe(Require(ProviderIdOf(mailbox))));

	public Task DeleteMailboxAsync(Account account, Mailbox mailbox, CancellationToken ct)
	{
		mailboxes.Remove(ProviderIdOf(mailbox));
		return Task.CompletedTask;
	}

	private readonly record struct Located(
		FakeMailbox Mailbox,
		string OccurrenceId,
		FakeMessage Message,
		MessageOccurrenceRef Reference
	);
}

public sealed record FakeMailbox(string ProviderMailboxId, SpecialUse SpecialUse)
{
	public Dictionary<string, FakeMessage> Messages { get; } = [];

	/// <summary>Occurrence ids the server will report as gone on the next sync.</summary>
	public List<string> Removed { get; } = [];
}

public sealed class FakeMessage(Guid messageId, DateTimeOffset receivedAt)
{
	public Guid MessageId { get; } = messageId;
	public DateTimeOffset ReceivedAt { get; } = receivedAt;
	public string Subject { get; set; } = "Fake message";
	public string? MessageIdHeader { get; set; }
	public bool IsRead { get; set; }
	public bool IsFlagged { get; set; }
	public byte[] RawBytes { get; set; } = [];
}
