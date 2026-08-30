using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;

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

	public Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct) =>
		oauth.AuthenticateAsync(account, ct);

	public async Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(
		Account account,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var response = await service.Users.Labels.List(UserId).ExecuteAsync(ct);

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
		var response = await request.ExecuteAsync(ct);
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

		var page = await request.ExecuteAsync(ct);
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
		if (cursor is not GmailHistoryCursor history)
		{
			throw new ArgumentException("Gmail sync requires a Gmail history cursor.", nameof(cursor));
		}

		var service = await ServiceAsync(account, ct);
		var request = service.Users.History.List(UserId);
		request.StartHistoryId = ulong.Parse(history.HistoryId);
		request.PageToken = continuation;
		request.MaxResults = SyncPageSize;

		try
		{
			var page = await request.ExecuteAsync(ct);
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
				changedMessageIds.Select(id => new Message { Id = id }),
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

	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.Users.Messages.Get(UserId, occurrence.ProviderOccurrenceId);
		request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
		var message = await request.ExecuteAsync(ct);
		return new RawMessageResult(FromBase64Url(message.Raw));
	}

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(Account account, CancellationToken ct) =>
		Task.FromResult(new AttachmentConstraints(null, 35 * 1024 * 1024, account.AttachmentSizeLimitOverride, false));

	private async Task<GmailService> ServiceAsync(Account account, CancellationToken ct)
	{
		var credential = await oauth.AuthorizeAsync(account, ct);
		return new GmailService(new BaseClientService.Initializer
		{
			HttpClientInitializer = credential,
			ApplicationName = "MyloMail",
		});
	}

	private static async Task<IReadOnlyList<MessageDto>> MessagesAsync(
		GmailService service,
		IEnumerable<Message> summaries,
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
			messages.Add(ToDto(await request.ExecuteAsync(ct)));
		}

		return messages;
	}

	private static MessageDto ToDto(Message message)
	{
		var headers = (message.Payload?.Headers ?? [])
			.Where(header => header.Name is not null)
			.GroupBy(header => header.Name!, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
		var labels = message.LabelIds ?? [];

		return new MessageDto
		{
			ProviderStableId = message.Id,
			Occurrences =
			[
				.. labels.Select(label => new MessageOccurrenceDto(label, message.Id!)),
			],
			MessageIdHeader = Header(headers, "Message-ID"),
			InReplyToHeader = Header(headers, "In-Reply-To"),
			ReferencesHeader = Header(headers, "References"),
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
		};
	}

	private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
		headers.TryGetValue(name, out var value) ? value : null;

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

	private static byte[] FromBase64Url(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return [];
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
