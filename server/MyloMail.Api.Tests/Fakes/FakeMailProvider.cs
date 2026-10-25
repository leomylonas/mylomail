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
public sealed class FakeMailProvider : IMailProvider, IIdleMailProvider, IAccountBackfillProvider
{
	private const int DefaultPageSize = 50;

	private readonly Dictionary<string, FakeMailbox> mailboxes = [];
	private readonly HashSet<string> omitted = [];
	private readonly Dictionary<string, MutationProblemDetails> forcedBatchFailures = [];
	private Exception? batchFailure;
	private int batchSuccessesBeforeFailure;
	private Exception? sendFailure;
	private Exception? fetchRawMessageFailure;
	private Exception? draftPushFailure;
	private Exception? mailboxOperationFailure;
	private Exception? deleteDraftFailure;
	private Exception? listMailboxesFailure;
	private MailboxTopologyResult? nextTopologyResult;
	private Exception? integrityFailure;
	private Exception? changeStreamFailure;
	private int draftPushSuccessesBeforeFailure;
	private string? authFailure;
	private readonly Dictionary<string, DraftResult> draftsByStableMessageId = [];
	private MutationProblemDetails? authFailureProblem;
	private long occurrenceSequence;

	/// <summary>Bumped whenever the fake server invalidates outstanding cursors.</summary>
	private int cursorGeneration = 1;

	public int SyncPageSize { get; set; } = DefaultPageSize;
	private long providerChangeSequence = 1;

	public FakeMailProvider(ProviderCapabilities capabilities)
	{
		Capabilities = capabilities;
	}

	public ProviderType Type => Capabilities.Type;

	public ProviderCapabilities Capabilities { get; }
	public TimeSpan IdleCancellationDelay { get; set; }
	public List<string?> TopologyCursors { get; } = [];
	public Func<MessageOccurrenceRef, Task>? BeforeFetchRawMessageReturnAsync { get; set; }
	public Func<Task>? BeforeInitialSyncReturnAsync { get; set; }
	public Func<Task>? BeforeChangeStreamReturnAsync { get; set; }
	public Func<Task>? BeforeIntegritySnapshotReturnAsync { get; set; }
	public TaskCompletionSource<bool> IdleStarted { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource<bool> IdleCancellationObserved { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource<bool> IdleFinished { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

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

	/// <summary>
	/// Makes the fake report a specific, caller-supplied problem for one occurrence's batch
	/// item instead of applying the mutation — for exercising a failure whose exact
	/// <see cref="MutationProblemDetails.Category"/>/<see cref="ProblemDetails.Extensions"/>
	/// shape (a rejected-certificate problem, say) needs to be controlled precisely, rather
	/// than whatever an ordinary not-found/omitted failure happens to carry.
	/// </summary>
	public void FailNextBatchItemWith(string providerOccurrenceId, MutationProblemDetails problem) =>
		forcedBatchFailures[providerOccurrenceId] = problem;

	/// <summary>Makes a later provider-native mutation batch throw at its call boundary.</summary>
	public void FailNextBatchWith(Exception failure, int successesBeforeFailure = 0)
	{
		batchFailure = failure;
		batchSuccessesBeforeFailure = successesBeforeFailure;
	}

	/// <summary>Makes the next send throw, so a rejection path can be exercised.</summary>
	public void FailSendWith(Exception failure) => sendFailure = failure;

	/// <summary>Makes the next raw-message fetch throw, so a content-fetch failure path can be exercised.</summary>
	public void FailFetchRawMessageWith(Exception failure) => fetchRawMessageFailure = failure;

	/// <summary>Makes the next folder create/rename/move/delete throw, so a provider rejection
	/// (a duplicate name, a namespace it won't accept) can be exercised.</summary>
	public void FailMailboxOperationWith(Exception failure) => mailboxOperationFailure = failure;

	/// <summary>One-shot: fails the next <see cref="SyncMailboxTopologyAsync"/> call, then clears.</summary>
	public void FailListMailboxesWith(Exception failure) => listMailboxesFailure = failure;

	/// <summary>Overrides one topology pass, then returns to the fake's full snapshot.</summary>
	public void ReturnNextTopologyResult(MailboxTopologyResult result) => nextTopologyResult = result;

	public void FailNextIntegrityWith(Exception failure) => integrityFailure = failure;

	public void FailNextChangeStreamWith(Exception failure) => changeStreamFailure = failure;

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

	/// <summary>Makes the next remote-draft delete throw, so a best-effort cleanup failure
	/// path can be exercised without it blocking the caller.</summary>
	public void FailDeleteDraftWith(Exception failure) => deleteDraftFailure = failure;

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
		mailbox.Messages[occurrenceId] = new FakeMessage(messageId, receivedAt)
		{
			MessageIdHeader = $"<{messageId:N}@fake.test>",
		};
		return occurrenceId;
	}

	public ProviderCursorState CurrentCursor() =>
		Type switch
		{
			ProviderType.Gmail => new GmailHistoryCursor(cursorGeneration.ToString()),
			ProviderType.Microsoft365 => new GraphDeltaCursor($"delta-{cursorGeneration}"),
			_ => new ImapUidCursor(
				(uint)cursorGeneration,
				(uint)occurrenceSequence,
				(ulong)Volatile.Read(ref providerChangeSequence),
				null
			),
		};

	private string NextOccurrenceId()
	{
		Interlocked.Increment(ref providerChangeSequence);
		return $"occ-{Interlocked.Increment(ref occurrenceSequence)}";
	}

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
	public Task<AuthResult> ReauthenticateAsync(Account account, CancellationToken ct) =>
		AuthenticateAsync(account, ct);

	public Task<MailboxTopologyResult> SyncMailboxTopologyAsync(
		Account account,
		string? cursor,
		CancellationToken ct
	)
	{
		TopologyCursors.Add(cursor);
		if (listMailboxesFailure is { } failure)
		{
			listMailboxesFailure = null;
			throw failure;
		}

		if (nextTopologyResult is { } configured)
		{
			nextTopologyResult = null;
			return Task.FromResult(configured);
		}

		IReadOnlyList<MailboxDto> mailboxes = [.. this.mailboxes.Values.Select(Describe)];
		return Task.FromResult(
			new MailboxTopologyResult(mailboxes, [], null, IsFullSnapshot: true)
		);
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

	/// <summary>How many folder walks were asked for. Recovery must not make any (it asks for one message).</summary>
	public int InitialSyncMailboxCalls { get; private set; }

	/// <summary>The lookups recovery made, one per message looked for.</summary>
	public List<MessageLookup> Lookups { get; } = [];

	/// <summary>Makes the next lookups fail, as a throttled or erroring provider would.</summary>
	public Exception? LookupFailure { get; set; }

	public Task<IReadOnlyList<MessageDto>> FindMessageAsync(Account account, MessageLookup lookup, CancellationToken ct)
	{
		Lookups.Add(lookup);
		if (LookupFailure is Exception failure)
		{
			throw failure;
		}

		var knownIds = lookup.KnownOccurrences.Select(known => known.ProviderOccurrenceId).ToHashSet(StringComparer.Ordinal);
		var found = new List<MessageDto>();
		foreach (var mailboxState in mailboxes.Values)
		{
			foreach (var (occurrenceId, message) in mailboxState.Messages)
			{
				var dto = ToDto(mailboxState.ProviderMailboxId, occurrenceId, message);
				if (
					(lookup.ProviderStableId is not null && dto.ProviderStableId == lookup.ProviderStableId)
					|| knownIds.Contains(occurrenceId)
					|| (lookup.MessageIdHeader is not null && message.MessageIdHeader == lookup.MessageIdHeader)
				)
				{
					found.Add(dto);
				}
			}
		}

		// Archived Gmail mail has no label, so it is found by id or header and has no occurrences.
		foreach (var message in unlabelled)
		{
			var dto = ToDto(string.Empty, string.Empty, message) with { Occurrences = [] };
			if (
				(lookup.ProviderStableId is not null && dto.ProviderStableId == lookup.ProviderStableId)
				|| (lookup.MessageIdHeader is not null && message.MessageIdHeader == lookup.MessageIdHeader)
			)
			{
				found.Add(dto);
			}
		}

		return Task.FromResult<IReadOnlyList<MessageDto>>(found);
	}

	public async Task<InitialSyncPage> InitialSyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	)
	{
		InitialSyncMailboxCalls++;
		var mailboxState = Require(ProviderIdOf(mailbox));
		IEnumerable<KeyValuePair<string, FakeMessage>> matching = mailboxState
			.Messages.OrderByDescending(kv => kv.Value.ReceivedAt);
		if (mode == InitialSyncMode.LastNMessages && bound is int count)
		{
			matching = matching.Take(count);
		}
		var bounded = matching.ToList();
		var offset = int.TryParse(resumeToken, out var parsedOffset) ? parsedOffset : 0;
		var page = bounded.Skip(offset).Take(pageSize).ToList();
		var consumed = offset + page.Count;
		var hasMore = consumed < bounded.Count;

		if (BeforeInitialSyncReturnAsync is not null)
		{
			await BeforeInitialSyncReturnAsync();
		}
		return new InitialSyncPage(
			[.. page.Select(kv => ToDto(mailboxState.ProviderMailboxId, kv.Key, kv.Value))],
			hasMore ? consumed.ToString() : null,
			hasMore,
			bounded.Count
		);
	}

	private readonly List<FakeMessage> unlabelled = [];

	/// <summary>
	/// The canonical messages the account-wide walk has handed out, in order, across every page
	/// and every walk. A message fetched twice appears twice, so a test can assert that a message
	/// wearing several labels is fetched once per walk rather than once per label.
	/// </summary>
	public List<Guid> AccountWalkMessageIds { get; } = [];

	/// <summary>Pages the account-wide walk has served.</summary>
	public int AccountWalkPages { get; private set; }

	/// <summary>Caps the page the account-wide walk serves, whatever size it is asked for.</summary>
	public int? AccountWalkPageLimit { get; set; }

	/// <summary>The server's clock for <c>LastNMonths</c>, so a test can pin what "N months ago" means.</summary>
	public DateTimeOffset ServerNow { get; set; } = DateTimeOffset.UtcNow;

	/// <summary>
	/// Places a message on the server with no label at all — archived mail on Gmail, which
	/// belongs to no mailbox and is reachable only through the account-wide walk.
	/// </summary>
	public void SeedMessageWithoutLabels(Guid messageId, DateTimeOffset receivedAt) =>
		unlabelled.Add(new FakeMessage(messageId, receivedAt) { MessageIdHeader = $"<{messageId:N}@fake.test>" });

	private sealed record WalkMessage(
		Guid MessageId,
		DateTimeOffset ReceivedAt,
		FakeMessage Message,
		IReadOnlyList<(string ProviderMailboxId, string OccurrenceId)> Memberships
	);

	public async Task<InitialSyncPage> InitialSyncAccountAsync(
		Account account,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	)
	{
		// One canonical message per FakeMessage.MessageId, however many mailboxes hold it — the
		// way a Gmail message is one message wearing several labels.
		var canonical = mailboxes
			.Values.SelectMany(mailbox =>
				mailbox.Messages.Select(pair => (mailbox.ProviderMailboxId, OccurrenceId: pair.Key, Message: pair.Value))
			)
			.GroupBy(item => item.Message.MessageId)
			.Select(group => new WalkMessage(
				group.Key,
				group.Max(item => item.Message.ReceivedAt),
				group.First().Message,
				[.. group.Select(item => (item.ProviderMailboxId, item.OccurrenceId))]
			))
			.Concat(
				unlabelled.Select(message => new WalkMessage(message.MessageId, message.ReceivedAt, message, []))
			)
			.OrderByDescending(item => item.ReceivedAt)
			.ThenBy(item => item.MessageId)
			.ToList();
		if (mode == InitialSyncMode.LastNMessages && bound is int count)
		{
			canonical = canonical.Take(count).ToList();
		}
		if (mode == InitialSyncMode.LastNMonths && bound is int months)
		{
			var since = ServerNow.AddMonths(-months);
			canonical = canonical.Where(item => item.ReceivedAt >= since).ToList();
		}

		var offset = int.TryParse(resumeToken, out var parsedOffset) ? parsedOffset : 0;
		var page = canonical.Skip(offset).Take(Math.Min(pageSize, AccountWalkPageLimit ?? pageSize)).ToList();
		var consumed = offset + page.Count;
		var hasMore = consumed < canonical.Count;
		AccountWalkPages++;
		AccountWalkMessageIds.AddRange(page.Select(item => item.MessageId));

		if (BeforeInitialSyncReturnAsync is not null)
		{
			await BeforeInitialSyncReturnAsync();
		}
		return new InitialSyncPage(
			[.. page.Select(ToWalkDto)],
			hasMore ? consumed.ToString() : null,
			hasMore,
			canonical.Count
		);
	}

	public int RecentListCalls { get; private set; }

	public long? AccountMessageTotal { get; set; }

	public Task<long?> GetAccountMessageTotalAsync(Account account, CancellationToken ct) =>
		Task.FromResult(AccountMessageTotal);

	private IEnumerable<WalkMessage> WalkMessagesNewestFirst() =>
		mailboxes
			.Values.SelectMany(mailbox =>
				mailbox.Messages.Select(pair => (mailbox.ProviderMailboxId, OccurrenceId: pair.Key, Message: pair.Value))
			)
			.GroupBy(item => item.Message.MessageId)
			.Select(group => new WalkMessage(
				group.Key,
				group.Max(item => item.Message.ReceivedAt),
				group.First().Message,
				[.. group.Select(item => (item.ProviderMailboxId, item.OccurrenceId))]
			))
			.Concat(unlabelled.Select(message => new WalkMessage(message.MessageId, message.ReceivedAt, message, [])))
			.OrderByDescending(item => item.ReceivedAt)
			.ThenBy(item => item.MessageId);

	public Task<IReadOnlyList<string>> ListRecentMessageIdsAsync(Account account, int count, CancellationToken ct)
	{
		RecentListCalls++;
		return Task.FromResult<IReadOnlyList<string>>(
			[.. WalkMessagesNewestFirst().Take(count).Select(item => ToWalkDto(item).ProviderStableId!)]
		);
	}

	public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
		Account account,
		IReadOnlyList<string> providerStableIds,
		CancellationToken ct
	) =>
		Task.FromResult<IReadOnlyList<MessageDto>>(
			[
				.. WalkMessagesNewestFirst()
					.Select(ToWalkDto)
					.Where(dto => dto.ProviderStableId is not null && providerStableIds.Contains(dto.ProviderStableId)),
			]
		);

	private MessageDto ToWalkDto(WalkMessage item)
	{
		var memberships = item.Memberships;
		var dto = ToDto(
			memberships.Count > 0 ? memberships[0].ProviderMailboxId : string.Empty,
			memberships.Count > 0 ? memberships[0].OccurrenceId : string.Empty,
			item.Message
		);
		return dto with
		{
			Occurrences =
			[
				.. memberships.Select(membership => new MessageOccurrenceDto(
					membership.ProviderMailboxId,
					membership.OccurrenceId
				)),
			],
		};
	}

	public async Task<SyncResult> SyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		ProviderCursorState? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		if (BeforeChangeStreamReturnAsync is not null)
		{
			await BeforeChangeStreamReturnAsync();
		}

		if (changeStreamFailure is { } failure)
		{
			changeStreamFailure = null;
			throw failure;
		}

		if (cursor is not null && CursorGenerationOf(cursor) != cursorGeneration)
		{
			throw new ProviderCursorInvalidException(
				$"cursor for '{ProviderIdOf(mailbox)}' predates a server-side invalidation"
			);
		}

		// An account-scoped stream reports the whole account in one walk, as Gmail's history
		// does: a page is not confined to the mailbox whose loop happened to poll it. Modelling
		// it per-mailbox would make the fake agree with a folder-scoped reading of §3 that no
		// account-scoped provider actually offers.
		var accountScoped = Capabilities.ChangeStreamScope == ChangeStreamScope.Account;
		var sources = accountScoped
			? mailboxes.Values.OrderBy(m => m.ProviderMailboxId, StringComparer.Ordinal).ToList()
			: [Require(ProviderIdOf(mailbox))];
		var occurrences = sources
			.SelectMany(source => source.Messages.Select(message => (Source: source, Occurrence: message)))
			.ToList();
		// A page that reports a flags-only change carries no message upserts: that is what makes it flags-only.
		var page = flagChangesToReport.Count > 0
			? []
			: occurrences.Skip(int.TryParse(continuation, out var offset) ? offset : 0).Take(SyncPageSize).ToList();
		var consumed = (int.TryParse(continuation, out var seen) ? seen : 0) + page.Count;
		var more = flagChangesToReport.Count == 0 && consumed < occurrences.Count;

		var removed = sources
			.SelectMany(source => source.Removed.Select(item => (Source: source, Removal: item)))
			.AsEnumerable();
		if (Capabilities.ReportsExpungesIncrementally
			&& cursor is ImapUidCursor { HighestModSeq: ulong highestModSeq })
		{
			removed = removed.Where(item => item.Removal.Value > highestModSeq);
		}

		return new SyncResult(
			// Null while the walk is incomplete, unless this provider's cursor is monotone
			// over what has already been returned.
			more && !Capabilities.AdvancesCursorMidWalk ? null : CurrentCursor(),
			more ? consumed.ToString() : null,
			[.. page.Select(item => ToDto(item.Source.ProviderMailboxId, item.Occurrence.Key, item.Occurrence.Value))],
			TakeFlagChanges(),
			[
				.. removed.Select(item =>
					new OccurrenceRemoval(item.Source.ProviderMailboxId, item.Removal.Key)
				),
			]
		);
	}

	public async Task<MailboxIntegritySnapshot> GetMailboxIntegritySnapshotAsync(
		Account account,
		Mailbox mailbox,
		IReadOnlyList<MessageOccurrenceRef> knownOccurrences,
		CancellationToken ct
	)
	{
		if (BeforeIntegritySnapshotReturnAsync is not null)
		{
			await BeforeIntegritySnapshotReturnAsync();
		}

		if (integrityFailure is { } failure)
		{
			integrityFailure = null;
			throw failure;
		}

		var source = Require(ProviderIdOf(mailbox));
		var flags = Capabilities.SupportsIncrementalFlagChanges
			? []
			: source.Messages.Select(pair => new OccurrenceFlagChange(
				source.ProviderMailboxId,
				pair.Key,
				pair.Value.IsRead,
				pair.Value.IsFlagged
			)).ToList();
		return new MailboxIntegritySnapshot(source.Messages.Keys.ToHashSet(), flags);
	}

	private readonly List<OccurrenceFlagChange> flagChangesToReport = [];

	/// <summary>The next change-stream page reports this read/flag change on its own, as IMAP does for a flags-only change.</summary>
	public void ReportFlagChange(string providerMailboxId, string providerOccurrenceId, bool isRead, bool? isFlagged = null) =>
		flagChangesToReport.Add(new OccurrenceFlagChange(providerMailboxId, providerOccurrenceId, isRead, isFlagged));

	private IReadOnlyList<OccurrenceFlagChange> TakeFlagChanges()
	{
		var taken = flagChangesToReport.ToList();
		flagChangesToReport.Clear();
		return taken;
	}

	private static int CursorGenerationOf(ProviderCursorState cursor) =>
		cursor switch
		{
			GmailHistoryCursor gmail => int.Parse(gmail.HistoryId),
			GraphDeltaCursor graph => int.Parse(graph.DeltaLink.Split('-')[^1]),
			ImapUidCursor imap => (int)imap.UidValidity,
			_ => throw new InvalidOperationException($"unhandled cursor kind '{cursor.Kind}'"),
		};

	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct,
		int? maximumBytes = null
	)
	{
		if (fetchRawMessageFailure is Exception failure)
		{
			fetchRawMessageFailure = null;
			throw failure;
		}

		var found = Locate(occurrence.ProviderOccurrenceId)
			?? throw new InvalidOperationException("no such occurrence");
		if (maximumBytes is { } maximum && found.Message.RawBytes.Length > maximum)
		{
			throw new InvalidOperationException($"Provider content exceeds the {maximum}-byte limit.");
		}
		if (BeforeFetchRawMessageReturnAsync is not null)
		{
			await BeforeFetchRawMessageReturnAsync(occurrence);
		}
		return new RawMessageResult(found.Message.RawBytes);
	}

	public AttachmentConstraints AttachmentConstraintsToReturn { get; set; } =
		new(null, null, null, IsUnknown: true);

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(
		Account account,
		CancellationToken ct
	) => Task.FromResult(AttachmentConstraintsToReturn);
	public async Task WaitForMailboxChangeAsync(
		Account account,
		Mailbox mailbox,
		CancellationToken ct
	)
	{
		IdleStarted.TrySetResult(true);
		try
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			IdleCancellationObserved.TrySetResult(true);
			await Task.Delay(IdleCancellationDelay);
			throw;
		}
		finally
		{
			IdleFinished.TrySetResult(true);
		}
	}

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
					found.Mailbox.Removed[found.OccurrenceId] =
						(ulong)Interlocked.Increment(ref providerChangeSequence);

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
					found.Mailbox.Removed[found.OccurrenceId] =
						(ulong)Interlocked.Increment(ref providerChangeSequence);
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
		if (batchFailure is { } failure)
		{
			if (batchSuccessesBeforeFailure > 0)
			{
				batchSuccessesBeforeFailure--;
			}
			else
			{
				batchFailure = null;
				throw failure;
			}
		}
		var items = new List<BatchItemResult>(refs.Count);

		foreach (var reference in refs)
		{
			if (omitted.Contains(reference.ProviderOccurrenceId))
			{
				continue;
			}

			if (forcedBatchFailures.Remove(reference.ProviderOccurrenceId, out var forcedProblem))
			{
				items.Add(
					new BatchItemResult(reference.MessageId, reference.MailboxId, Succeeded: false, forcedProblem, [])
				);
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
			From = message.From,
			ReceivedAt = message.ReceivedAt,
			IsRead = message.IsRead,
			IsFlagged = message.IsFlagged,
			Subject = message.Subject,
		};

	private Exception? importFailure;
	private Exception? importFailureAfterStoring;

	/// <summary>Every message <see cref="ImportMessageAsync"/> stored, in order.</summary>
	public List<(string ProviderMailboxId, byte[] Raw, bool IsRead, bool IsFlagged)> Imported { get; } = [];

	/// <summary>The next import throws before anything is stored: a definite failure.</summary>
	public void FailImportWith(Exception failure) => importFailure = failure;

	/// <summary>The next import stores the message and then throws: the server did it, the answer was lost.</summary>
	public void FailImportAfterStoringWith(Exception failure) => importFailureAfterStoring = failure;

	public Task ImportMessageAsync(
		Account account,
		Mailbox target,
		byte[] rawMime,
		bool isRead,
		bool isFlagged,
		CancellationToken ct
	)
	{
		if (!Capabilities.CanImportMessages)
		{
			throw new NotSupportedException("This provider cannot store a received message as it was.");
		}

		if (importFailure is Exception failure)
		{
			importFailure = null;
			throw failure;
		}

		var mailbox = Require(ProviderIdOf(target));
		using var stream = new MemoryStream(rawMime);
		var parsed = MimeKit.MimeMessage.Load(stream);
		var occurrenceId = NextOccurrenceId();
		mailbox.Messages[occurrenceId] = new FakeMessage(Guid.NewGuid(), DateTimeOffset.UtcNow)
		{
			MessageIdHeader = parsed.MessageId is { Length: > 0 } id ? $"<{id}>" : null,
			Subject = parsed.Subject ?? string.Empty,
			IsRead = isRead,
			IsFlagged = isFlagged,
			RawBytes = rawMime,
		};
		Imported.Add((mailbox.ProviderMailboxId, rawMime, isRead, isFlagged));

		if (importFailureAfterStoring is Exception afterStoring)
		{
			importFailureAfterStoring = null;
			throw afterStoring;
		}

		return Task.CompletedTask;
	}

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

	/// <summary>Registers the Gmail draft container that encloses an externally observed message.</summary>
	public void SeedDraftContainer(string providerMessageId) =>
		draftsByStableMessageId[providerMessageId] = new DraftResult(NextOccurrenceId(), "rev-1", providerMessageId);

	/// <summary>
	/// Makes draft updates behave like a real server's: each push mints a new revision and an update
	/// against any other revision than the current one is a conflict. Off by default, where every
	/// push returns "rev-1".
	/// </summary>
	public bool EnforceDraftRevisions { get; set; }

	/// <summary>Runs once a draft push has changed the server but before it returns, so a test can hold one there.</summary>
	public Func<Task>? DuringDraftPushAsync { get; set; }

	private int draftRevisionCounter;

	public async Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		if (EnforceDraftRevisions && expectedRevision is not null)
		{
			var current = draft.StableMessageId is { } key ? draftsByStableMessageId.GetValueOrDefault(key) : null;
			if (current?.ProviderRevision != expectedRevision)
			{
				throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
			}
		}

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
		var result = new DraftResult(
			providerId,
			EnforceDraftRevisions ? $"rev-{Interlocked.Increment(ref draftRevisionCounter) + 1}" : "rev-1",
			providerId
		);
		if (draft.StableMessageId is { } stableMessageId)
		{
			draftsByStableMessageId[stableMessageId] = result;
		}

		// After the server has changed and before the caller has recorded it: the window in which
		// a second push starts from a revision that no longer exists.
		if (DuringDraftPushAsync is not null)
		{
			await DuringDraftPushAsync();
		}

		return result;
	}

	/// <summary>Puts a draft on the server as an earlier push would have left it.</summary>
	public void SeedDraft(string stableMessageId, string providerDraftId, string revision) =>
		draftsByStableMessageId[stableMessageId] = new DraftResult(providerDraftId, revision, providerDraftId);

	public Task<DraftResult?> FindDraftAsync(
		Account account,
		string stableMessageId,
		CancellationToken ct,
		int? maximumBytes = null
	) => Task.FromResult(draftsByStableMessageId.GetValueOrDefault(stableMessageId));

	public Task<DraftResult?> FindDraftByMessageIdAsync(Account account, string providerMessageId, CancellationToken ct) =>
		Task.FromResult(draftsByStableMessageId.Values.SingleOrDefault(d => d.ProviderMessageId == providerMessageId));

	public Task DeleteDraftAsync(Account account, string providerDraftId, string? expectedRevision, CancellationToken ct)
	{
		if (deleteDraftFailure is Exception failure)
		{
			deleteDraftFailure = null;

			throw failure;
		}
		return Task.CompletedTask;
	}

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

	/// <summary>Occurrence ids and revisions the server reports as gone on incremental sync.</summary>
	public Dictionary<string, ulong> Removed { get; } = [];
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
	public IReadOnlyList<Address> From { get; set; } = [];
}
