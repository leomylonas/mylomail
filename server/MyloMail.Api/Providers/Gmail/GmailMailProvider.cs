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
/// for each label (§1, §3).
/// </summary>
public sealed partial class GmailMailProvider(
	GmailOAuthAuthenticator oauth,
	IProviderMailboxResolver mailboxes
) : IMailProvider
{
	private const string UserId = "me";
	private const int SyncPageSize = 200;

	public ProviderType Type => ProviderType.Gmail;

	public ProviderCapabilities Capabilities { get; } = new()
	{
		Type = ProviderType.Gmail,
		ChangeStreamScope = ChangeStreamScope.Account,
		ImapTier = ImapCapabilityTier.NotApplicable,
		ReportsMailboxCounts = true,
		ReportsDestinationIdOnMove = true,
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
			await service.Users.GetProfile(UserId).ExecuteAsync(ct);
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

	public async Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(
		Account account,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var response = await service.Users.Labels.List(UserId).ExecuteThrottleAwareAsync(ct);

		return
		[
			.. (response.Labels ?? [])
				.Where(label => label.Id is not null && label.Name is not null)
				.Select(label => new MailboxDto
				{
					ProviderMailboxId = label.Id!,
					Name = label.Name!,
					// Gmail's labels are flat. Any visual hierarchy is local and derived (§1).
					ParentProviderMailboxId = null,
					SpecialUse = SpecialUseOf(label.Id!),
					IsSubscribed = label.LabelListVisibility != "labelHide",
					TotalCount = label.MessagesTotal,
					UnreadCount = label.MessagesUnread,
				}),
		];
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
		var request = service.Users.Messages.List(UserId);
		request.LabelIds = new Google.Apis.Util.Repeatable<string>([ProviderMailboxId(mailbox)]);
		request.PageToken = resumeToken;
		request.MaxResults = Math.Min(pageSize, bound ?? pageSize);
		if (mode == InitialSyncMode.LastNMonths && bound is int months)
		{
			request.Q = $"after:{DateTimeOffset.UtcNow.AddMonths(-months).ToUnixTimeSeconds()}";
		}

		var page = await request.ExecuteThrottleAwareAsync(ct);
		var messages = await MessagesAsync(service, page.Messages ?? [], ct);
		return new InitialSyncPage(
			messages,
			page.NextPageToken,
			page.NextPageToken is not null,
			page.ResultSizeEstimate is long total ? checked((int)total) : null
		);
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

		var request = service.Users.History.List(UserId);
		request.StartHistoryId = ulong.Parse(history.HistoryId);
		request.PageToken = continuation;
		request.MaxResults = SyncPageSize;

		try
		{
			var page = await request.ExecuteThrottleAwareAsync(ct);
			var changedMessageIds = (page.History ?? [])
				.SelectMany(item => item.MessagesAdded ?? [])
				.Select(item => item.Message?.Id)
				.Concat(
					(page.History ?? [])
						.SelectMany(item => item.LabelsAdded ?? [])
						.Select(item => item.Message?.Id)
				)
				.Concat(
					(page.History ?? [])
						.SelectMany(item => item.LabelsRemoved ?? [])
						.Select(item => item.Message?.Id)
				)
				.Where(id => id is not null)
				.Cast<string>()
				.Distinct()
				.ToList();

			var upserted = await MessagesAsync(
				service,
				changedMessageIds.Select(id => new GmailMessage { Id = id }),
				ct
			);
			IReadOnlyList<OccurrenceRemoval> removed =
			[
				.. (page.History ?? [])
					.SelectMany(item => item.LabelsRemoved ?? [])
					.SelectMany(change =>
						(change.LabelIds ?? []).Select(labelId => new OccurrenceRemoval(
							labelId,
							change.Message?.Id ?? string.Empty
						))
					)
					.Where(change => change.ProviderOccurrenceId.Length > 0),
			];

			return new SyncResult(
				page.NextPageToken is null && page.HistoryId is not null
					? new GmailHistoryCursor(page.HistoryId.ToString()!)
					: null,
				page.NextPageToken,
				upserted,
				[],
				removed
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
			var credential = await oauth.AuthorizeAsync(account, ct);
			var service = new GmailService(new BaseClientService.Initializer
			{
				HttpClientInitializer = credential,
				ApplicationName = "MyloMail",
			});
			service.AttachThrottleTracker(new GmailThrottleTracker());
			return service;
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

	private static async Task<IReadOnlyList<MessageDto>> MessagesAsync(
		GmailService service,
		IEnumerable<GmailMessage> summaries,
		CancellationToken ct
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
			messages.Add(ToDto(await request.ExecuteThrottleAwareAsync(ct)));
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
