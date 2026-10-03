using System.Globalization;
using System.Text;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MimeKit;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;
using GmailMessagePart = Google.Apis.Gmail.v1.Data.MessagePart;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// Gmail's label-based mail API. Gmail history is account-scoped, so callers must persist the
/// returned <see cref="GmailHistoryCursor"/> once per account rather than inventing a cursor
/// for each label (§1, §3). Historical backfill is account-scoped too
/// (<see cref="IAccountBackfillProvider"/>); the per-label
/// <see cref="InitialSyncMailboxAsync"/> remains for labels discovered after that walk.
/// </summary>
/// <remarks>
/// Every request an instance makes is charged to <see cref="GmailRequestBudget"/> under the
/// account it was built for, so backfill, the change stream, content downloads and mutations
/// of one account share one quota bucket. The reactive 403/429 translation stays as the
/// backstop for what pacing cannot see.
/// </remarks>
public sealed partial class GmailMailProvider(
	GmailOAuthAuthenticator oauth,
	IProviderMailboxResolver mailboxes,
	GmailRequestBudget? budget = null
) : IMailProvider, IAccountBackfillProvider
{
	private const string UserId = "me";
	private const int SyncPageSize = 100;
	private const string InitialSyncCursorPrefix = "mylomail-gmail-initial-v1.";

	private readonly GmailRequestBudget requestBudget = budget ?? GmailRequestBudget.Shared;

	public ProviderType Type => ProviderType.Gmail;

	public ProviderCapabilities Capabilities { get; } = new()
	{
		Type = ProviderType.Gmail,
		ChangeStreamScope = ChangeStreamScope.Account,
		ImapTier = ImapCapabilityTier.NotApplicable,
		ReportsMailboxCounts = true,
		ReportsDestinationIdOnMove = true,
		RequiresCoverageBeforeInitialChangeStream = false,
		SupportsIncrementalFlagChanges = true,
		ReportsExpungesIncrementally = true,
		AdvancesCursorMidWalk = false,
		SupportsMultipleMailboxMembership = true,
		SupportsServerSideDrafts = true,
		DeletingMailboxDeletesMessages = false,
	};

	public async Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct)
	{
		var authorization = await oauth.AuthenticateAsync(account, ct);
		if (!authorization.Succeeded)
		{
			return authorization;
		}

		try
		{
			var service = await ServiceAsync(account, ct);
			await service.Users.GetProfile(UserId).ExecuteThrottleAwareAsync(ct);
			return authorization;
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
		{
			return new AuthResult(
				false,
				AuthState.NeedsReauth,
				new MutationProblemDetails
				{
					Title = "Gmail API unavailable",
					Detail = "Google denied Gmail API access. Enable the Gmail API for this OAuth project, then try again.",
					Category = ErrorCategory.Auth,
				}
			);
		}
	}

	public async Task<MailboxTopologyResult> SyncMailboxTopologyAsync(
		Account account,
		string? cursor,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var response = await service.Users.Labels.List(UserId).ExecuteThrottleAwareAsync(ct);

		var labels = response.Labels ?? [];
		var mailboxes = new List<MailboxDto>(labels.Count);
		foreach (var label in labels.Where(label => label.Id is not null && label.Name is not null))
		{
			// labels.list omits the message counts. Fetch each label's authoritative metadata
			// rather than mistaking its absent fields for an empty mailbox.
			var metadata = await service.Users.Labels.Get(UserId, label.Id!).ExecuteThrottleAwareAsync(ct);
			mailboxes.Add(ToMailboxDto(metadata));
		}

		return new MailboxTopologyResult(mailboxes, [], null, IsFullSnapshot: true);
	}

	public async Task<int> EstimateMailboxCountAsync(
		Account account,
		Mailbox mailbox,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.Users.Messages.List(UserId);
		request.LabelIds = new Google.Apis.Util.Repeatable<string>([ProviderMailboxId(mailbox)]);
		request.MaxResults = 1;
		var response = await request.ExecuteThrottleAwareAsync(ct);
		return checked((int)(response.ResultSizeEstimate ?? 0));
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
		var service = await ServiceAsync(account, ct);
		var cursor = InitialSyncCursor.Parse(resumeToken, mode, bound);
		var request = service.Users.Messages.List(UserId);
		request.LabelIds = new Google.Apis.Util.Repeatable<string>([ProviderMailboxId(mailbox)]);
		request.PageToken = cursor.ProviderPageToken;
		request.MaxResults = cursor.RequestLimit(pageSize);
		if (mode == InitialSyncMode.LastNMonths && bound is int months)
		{
			request.Q = $"after:{DateTimeOffset.UtcNow.AddMonths(-months).ToUnixTimeSeconds()}";
		}

		var page = await request.ExecuteThrottleAwareAsync(ct);
		var summaries = page.Messages ?? [];
		var messages = await MessagesAsync(service, summaries, ct);
		var continuation = cursor.Advance(page.NextPageToken, summaries.Count);
		int? estimatedTotal = page.ResultSizeEstimate is long total
			? checked((int)Math.Min(total, mode == InitialSyncMode.LastNMessages && bound is int count ? count : total))
			: null;
		return new InitialSyncPage(
			messages,
			continuation.ResumeToken,
			continuation.HasMore,
			estimatedTotal
		);
	}

	public async Task<InitialSyncPage> InitialSyncAccountAsync(
		Account account,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		return await AccountWalkPageAsync(
			service,
			resumeToken,
			mode,
			bound,
			pageSize,
			DateTimeOffset.UtcNow,
			ct
		);
	}

	/// <summary>
	/// One page of the account-wide walk: <c>messages.list</c> with <b>no label filter</b> and
	/// <c>includeSpamTrash</c>, then exactly one <c>format=full</c> get per listed id.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The old shape listed each label separately, so a message wearing INBOX, UNREAD, IMPORTANT
	/// and a category was fetched four times. Here the list is the account's own message
	/// sequence, each message is fetched once, and its label membership is read off the fetched
	/// message's <c>labelIds</c> (<see cref="ToDto"/>) for the caller to map onto its mailboxes.
	/// A message with no labels (archived mail) comes back with no occurrences.
	/// </para>
	/// <para>
	/// <b>Bounds apply to the walk as a whole.</b> <c>LastNMessages</c> consumes its count
	/// across pages from the newest listed message regardless of label; <c>LastNMonths</c>
	/// restricts the list with <c>after:</c>. Either way the coverage target is the account's
	/// newest messages, not each label's newest.
	/// </para>
	/// <para>
	/// A listed message that is gone by the time it is fetched (deleted between the list and
	/// the get, which a walk of tens of thousands of messages makes ordinary) is skipped and
	/// still counted as consumed. Failing the page instead would replay the list, which no
	/// longer contains it, so skipping loses nothing a replay would have kept.
	/// </para>
	/// </remarks>
	internal static async Task<InitialSyncPage> AccountWalkPageAsync(
		GmailService service,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		DateTimeOffset now,
		CancellationToken ct
	)
	{
		var cursor = InitialSyncCursor.Parse(resumeToken, mode, bound);
		var request = service.Users.Messages.List(UserId);
		request.IncludeSpamTrash = true;
		request.PageToken = cursor.ProviderPageToken;
		request.MaxResults = cursor.RequestLimit(pageSize);
		if (mode == InitialSyncMode.LastNMonths && bound is int months)
		{
			request.Q = $"after:{now.AddMonths(-months).ToUnixTimeSeconds()}";
		}

		var page = await request.ExecuteThrottleAwareAsync(ct);
		var listed = page.Messages ?? [];
		var messages = await MessagesAsync(
			service,
			listed.DistinctBy(summary => summary.Id, StringComparer.Ordinal),
			ct,
			skipMissing: true
		);
		var continuation = cursor.Advance(page.NextPageToken, listed.Count);
		int? estimatedTotal = page.ResultSizeEstimate is long total
			? checked((int)Math.Min(total, mode == InitialSyncMode.LastNMessages && bound is int count ? count : total))
			: null;
		return new InitialSyncPage(
			messages,
			continuation.ResumeToken,
			continuation.HasMore,
			estimatedTotal
		);
	}

	internal readonly record struct InitialSyncCursor(string? ProviderPageToken, int? Remaining)
	{
		public int RequestLimit(int pageSize) =>
			Remaining is int remaining ? Math.Min(pageSize, remaining) : pageSize;

		public (string? ResumeToken, bool HasMore) Advance(
			string? nextProviderPageToken,
			int consumed
		)
		{
			if (nextProviderPageToken is null)
			{
				return (null, false);
			}
			if (Remaining is not int remaining)
			{
				return (nextProviderPageToken, true);
			}

			var nextRemaining = Math.Max(0, remaining - consumed);
			return nextRemaining == 0
				? (null, false)
				: (Encode(nextProviderPageToken, nextRemaining), true);
		}

		public static InitialSyncCursor Parse(
			string? resumeToken,
			InitialSyncMode mode,
			int? bound
		)
		{
			if (resumeToken is null)
			{
				return new InitialSyncCursor(
					null,
					mode == InitialSyncMode.LastNMessages ? bound : null
				);
			}
			if (!resumeToken.StartsWith(InitialSyncCursorPrefix, StringComparison.Ordinal))
			{
				if (mode == InitialSyncMode.LastNMessages)
				{
					throw new InvalidOperationException("Invalid Gmail initial-sync continuation.");
				}
				return new InitialSyncCursor(resumeToken, null);
			}

			var payload = resumeToken[InitialSyncCursorPrefix.Length..];
			var separator = payload.IndexOf('.', StringComparison.Ordinal);
			if (
				separator <= 0
				|| !int.TryParse(
					payload.AsSpan(0, separator),
					NumberStyles.None,
					CultureInfo.InvariantCulture,
					out var remaining
				)
				|| remaining <= 0
			)
			{
				throw new InvalidOperationException("Invalid Gmail initial-sync continuation.");
			}

			try
			{
				var providerPageToken = Encoding.UTF8.GetString(FromBase64Url(payload[(separator + 1)..]));
				return new InitialSyncCursor(
					providerPageToken,
					mode == InitialSyncMode.LastNMessages ? remaining : null
				);
			}
			catch (FormatException ex)
			{
				throw new InvalidOperationException("Invalid Gmail initial-sync continuation.", ex);
			}
		}

		private static string Encode(string providerPageToken, int remaining)
		{
			var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(providerPageToken))
				.TrimEnd('=')
				.Replace('+', '-')
				.Replace('/', '_');
			return $"{InitialSyncCursorPrefix}{remaining.ToString(CultureInfo.InvariantCulture)}.{encoded}";
		}
	}

	public async Task<SyncResult> SyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		ProviderCursorState? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		if (cursor is null)
		{
			var profile = await service.Users.GetProfile(UserId).ExecuteThrottleAwareAsync(ct);
			return new SyncResult(
				new GmailHistoryCursor(profile.HistoryId?.ToString() ?? throw new InvalidOperationException("Gmail did not return a history id.")),
				null,
				[],
				[],
				[]
			);
		}
		if (cursor is not GmailHistoryCursor history)
		{
			throw new ArgumentException("Gmail sync requires a Gmail history cursor.", nameof(cursor));
		}

		if (!ulong.TryParse(history.HistoryId, out var requestedHistoryId))
		{
			throw new ProviderCursorInvalidException("Gmail history cursor is invalid.");
		}
		var request = service.Users.History.List(UserId);
		request.StartHistoryId = requestedHistoryId;
		request.PageToken = continuation;
		request.MaxResults = SyncPageSize;

		try
		{
			var page = await request.ExecuteThrottleAwareAsync(ct);
			if (
				page.HistoryId is { } currentHistoryId
				&& requestedHistoryId > currentHistoryId
			)
			{
				// Gmail may accept a syntactically valid future history id and return an
				// empty page instead of 404. Such a value cannot have been a committed local
				// cursor; treating it as valid would move the client backwards to the
				// response's current id without a triggered resynchronisation.
				throw new ProviderCursorInvalidException("Gmail history cursor is invalid.");
			}
			if (page.NextPageToken is null && page.HistoryId is null)
			{
				// A completed Gmail history page must return the mailbox's current history
				// id. An empty terminal response without it is how Gmail reports some
				// syntactically valid but unusable start ids; it cannot advance a durable
				// cursor safely.
				throw new ProviderCursorInvalidException("Gmail history cursor is invalid.");
			}
			var changes = GmailHistoryChanges.From(page.History ?? []);
			var upserted = await MessagesAsync(
				service,
				changes.ChangedMessageIds.Select(id => new GmailMessage { Id = id }),
				ct
			);
			var removed = FinalLabelRemovals(changes.LabelRemovals, upserted);

			return new SyncResult(
				page.NextPageToken is null && page.HistoryId is not null
					? new GmailHistoryCursor(page.HistoryId.ToString()!)
					: null,
				page.NextPageToken,
				upserted,
				[],
				removed,
				changes.PermanentlyDeletedMessageIds
			);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
		{
			throw new ProviderCursorInvalidException("Gmail history cursor has expired.", ex);
		}
	}

	public Task<MailboxIntegritySnapshot> GetMailboxIntegritySnapshotAsync(
		Account account,
		Mailbox mailbox,
		IReadOnlyList<MessageOccurrenceRef> knownOccurrences,
		CancellationToken ct
	) => throw new NotSupportedException("Gmail's history stream does not require periodic mailbox integrity reconciliation.");

	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct,
		int? maximumBytes = null
	)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.Users.Messages.Get(UserId, occurrence.ProviderOccurrenceId);
		request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
		var message = await request.ExecuteThrottleAwareAsync(ct);
		return new RawMessageResult(FromBase64Url(message.Raw, maximumBytes));
	}

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(Account account, CancellationToken ct) =>
		Task.FromResult(new AttachmentConstraints(null, 35 * 1024 * 1024, account.AttachmentSizeLimitOverride, false));

	private async Task<GmailService> ServiceAsync(Account account, CancellationToken ct)
	{
		try
		{
			return BuildService(account, await oauth.AuthorizeAsync(account, ct));
		}
		// Same gap pass 196/197 fixed for IMAP/SMTP: AuthorizeAsync throws this raw when a
		// refresh token is revoked or expired, but only GmailOAuthAuthenticator.AuthenticateAsync
		// (the initial account-setup path) translated it. Every other Gmail operation — sync,
		// mailboxes, send, drafts, mutations — funnels through this one method, so a revoked
		// token mid-session propagated uncaught past SyncJobs.GuardAsync's specific
		// ProviderAuthenticationException catch, landing in its generic fallback instead and
		// silently stopping the poll loop rather than setting AuthState.NeedsReauth (§3, §7).
		catch (TokenResponseException ex)
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}
	}

	/// <summary>
	/// The one place a <see cref="GmailService"/> is constructed, so no request can be made that
	/// is not charged to the account's <see cref="GmailRequestBudget"/> and not watched for a
	/// throttling response.
	/// </summary>
	internal GmailService BuildService(
		Account account,
		Google.Apis.Http.IConfigurableHttpClientInitializer? credential,
		Google.Apis.Http.IHttpClientFactory? httpClientFactory = null
	)
	{
		var initializer = new BaseClientService.Initializer
		{
			HttpClientInitializer = credential,
			ApplicationName = "MyloMail",
		};
		if (httpClientFactory is not null)
		{
			initializer.HttpClientFactory = httpClientFactory;
		}

		var service = new GmailService(initializer);
		service.AttachThrottleTracker(new GmailThrottleTracker());
		service.AttachQuotaMeter(account.Id, requestBudget);
		return service;
	}

	private static async Task<IReadOnlyList<MessageDto>> MessagesAsync(
		GmailService service,
		IEnumerable<GmailMessage> summaries,
		CancellationToken ct,
		bool skipMissing = false
	)
	{
		var messages = new List<MessageDto>();
		foreach (var summary in summaries)
		{
			if (summary.Id is null)
			{
				continue;
			}

			var request = service.Users.Messages.Get(UserId, summary.Id);
			request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
			try
			{
				messages.Add(ToDto(await request.ExecuteThrottleAwareAsync(ct)));
			}
			catch (GoogleApiException ex)
				when (skipMissing && ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
			{
				// Deleted since it was listed; see AccountWalkPageAsync.
			}
		}

		return messages;
	}

	internal static MessageDto ToDto(GmailMessage message)
	{
		var headers = (message.Payload?.Headers ?? [])
			.Where(header => header.Name is not null)
			.GroupBy(header => header.Name!, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
		var labels = message.LabelIds ?? [];

		return new MessageDto
		{
			ProviderStableId = message.Id,
			// Gmail's history id is monotonic for a message observation. The draft adapter uses
			// it to re-check the copy it read before replacing it, because Gmail exposes no
			// HTTP ETag precondition on draft updates.
			ProviderRevision = message.HistoryId?.ToString(),
			Occurrences =
			[
				.. labels.Select(label => new MessageOccurrenceDto(label, message.Id!)),
			],
			MessageIdHeader = Header(headers, "Message-ID"),
			InReplyToHeader = Header(headers, "In-Reply-To"),
			ReferencesHeader = Header(headers, "References"),
			ThreadId = message.ThreadId,
			// Gmail's Full format hands back every RFC 5322 header verbatim in Payload.Headers,
			// but leaves parsing them to the caller — unlike IMAP's own ENVELOPE (already
			// structured) and Graph's own typed from/toRecipients/etc. fields. Without this,
			// every Gmail message ingested (MessageIngestor.cs: message.From = dto.From, and
			// likewise To/Cc/Bcc) would persist with empty address lists: a blank sender/
			// recipients in the message list, "from:"/"to:"/"cc:" search never matching a single
			// Gmail message, and reply routing (ReplyToAddresses, preferred over From per §1)
			// silently falling through to an empty From.
			ReplyToAddresses = Addresses(Header(headers, "Reply-To")),
			From = Addresses(Header(headers, "From")),
			To = Addresses(Header(headers, "To")),
			Cc = Addresses(Header(headers, "Cc")),
			Bcc = Addresses(Header(headers, "Bcc")),
			Subject = Header(headers, "Subject") ?? string.Empty,
			Snippet = message.Snippet ?? string.Empty,
			ReceivedAt = message.InternalDate is long milliseconds
				? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
				: DateTimeOffset.MinValue,
			IsRead = !labels.Contains("UNREAD"),
			IsFlagged = labels.Contains("STARRED"),
			IsDraft = labels.Contains("DRAFT"),
			IsAnswered = false,
			SizeEstimate = message.SizeEstimate,

			// Full format's Payload already carries the whole MIME part tree, so this needs no
			// extra fetch — but without it this defaulted to false for every Gmail message
			// forever, since nothing re-announces the list once ContentAcquisition later
			// corrects it from the real MIME.
			HasNonInlineAttachments = HasNonInlineAttachment(message.Payload),
		};
	}

	internal static IReadOnlyList<OccurrenceRemoval> FinalLabelRemovals(
		IReadOnlyList<OccurrenceRemoval> removals,
		IReadOnlyList<MessageDto> upserted
	)
	{
		var membershipsAfterPage = upserted
			.SelectMany(message =>
				message.Occurrences.Select(occurrence => (occurrence.ProviderMailboxId, occurrence.ProviderOccurrenceId))
			)
			.ToHashSet();
		return removals
			.Where(removal =>
				!membershipsAfterPage.Contains((removal.ProviderMailboxId, removal.ProviderOccurrenceId))
			)
			.ToList();
	}

	internal static MailboxDto ToMailboxDto(Label label) =>
		new()
		{
			ProviderMailboxId = label.Id ?? throw new InvalidOperationException("Gmail did not return a label id."),
			Name = label.Name ?? throw new InvalidOperationException("Gmail did not return a label name."),
			// Gmail's labels are flat. Any visual hierarchy is local and derived (§1).
			ParentProviderMailboxId = null,
			SpecialUse = SpecialUseOf(label.Id!),
			IsSubscribed = label.LabelListVisibility != "labelHide",
			TotalCount = label.MessagesTotal,
			UnreadCount = label.MessagesUnread,
		};

	internal sealed record GmailHistoryChanges(
		IReadOnlyList<string> ChangedMessageIds,
		IReadOnlyList<string> PermanentlyDeletedMessageIds,
		IReadOnlyList<OccurrenceRemoval> LabelRemovals
	)
	{
		public static GmailHistoryChanges From(IEnumerable<History> history)
		{
			var deletedIds = history
				.SelectMany(item => item.MessagesDeleted ?? [])
				.Select(change => change.Message?.Id)
				.OfType<string>()
				.Distinct(StringComparer.Ordinal)
				.ToList();
			var deleted = deletedIds.ToHashSet(StringComparer.Ordinal);
			var changed = history
				.SelectMany(item => item.MessagesAdded ?? [])
				.Select(change => change.Message?.Id)
				.Concat(history.SelectMany(item => item.LabelsAdded ?? []).Select(change => change.Message?.Id))
				.Concat(history.SelectMany(item => item.LabelsRemoved ?? []).Select(change => change.Message?.Id))
				.OfType<string>()
				.Where(id => !deleted.Contains(id))
				.Distinct(StringComparer.Ordinal)
				.ToList();
			var removals = history
				.SelectMany(item => item.LabelsRemoved ?? [])
				.SelectMany(change =>
					(change.LabelIds ?? []).Select(labelId => new OccurrenceRemoval(
						labelId,
						change.Message?.Id ?? string.Empty
					))
				)
				.Where(change => change.ProviderOccurrenceId.Length > 0 && !deleted.Contains(change.ProviderOccurrenceId))
				.Distinct()
				.ToList();
			return new GmailHistoryChanges(changed, deletedIds, removals);
		}
	}

	private static bool HasNonInlineAttachment(GmailMessagePart? part)
	{
		if (part is null)
		{
			return false;
		}
		if (!string.IsNullOrEmpty(part.Filename))
		{
			var disposition = part.Headers?.FirstOrDefault(h =>
				string.Equals(h.Name, "Content-Disposition", StringComparison.OrdinalIgnoreCase)
			);
			if (!(disposition?.Value?.StartsWith("inline", StringComparison.OrdinalIgnoreCase) ?? false))
			{
				return true;
			}
		}
		return part.Parts?.Any(HasNonInlineAttachment) ?? false;
	}

	private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
		headers.TryGetValue(name, out var value) ? value : null;

	/// <summary>Parses an RFC 5322 address-list header value the same way IMAP's own envelope
	/// addresses are converted — a malformed value (rare, but real servers do send one) yields an
	/// empty list rather than throwing and losing the whole message.</summary>
	private static IReadOnlyList<Address> Addresses(string? headerValue) =>
		headerValue is not null && InternetAddressList.TryParse(headerValue, out var list)
			? [.. list.Mailboxes.Select(m => new Address(m.Name, m.Address))]
			: [];

	private static string ProviderMailboxId(Mailbox mailbox) =>
		mailbox.ProviderMailboxId
		?? throw new InvalidOperationException("Gmail mailboxes always have a provider id.");

	private static SpecialUse SpecialUseOf(string id) =>
		id switch
		{
			"INBOX" => SpecialUse.Inbox,
			"SENT" => SpecialUse.Sent,
			"DRAFT" => SpecialUse.Drafts,
			"TRASH" => SpecialUse.Trash,
			"SPAM" => SpecialUse.Junk,
			"IMPORTANT" => SpecialUse.Archive,
			_ => SpecialUse.None,
		};

	private static byte[] FromBase64Url(string? value, int? maximumBytes = null)
	{
		if (string.IsNullOrEmpty(value))
		{
			return [];
		}
		if (maximumBytes is { } maximum && value.Length > (long)maximum * 4 / 3 + 4)
		{
			throw new InvalidOperationException($"Provider content exceeds the {maximum}-byte limit.");
		}

		var padded = value.Replace('-', '+').Replace('_', '/');
		padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
		return Convert.FromBase64String(padded);
	}

	private static MutationProblemDetails Problem(string title, string detail, string? code = null) =>
		new()
		{
			Title = title,
			Detail = detail,
			Category = ErrorCategory.ProviderRejected,
			ProviderCode = code,
		};
}
