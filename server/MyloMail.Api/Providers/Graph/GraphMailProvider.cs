using Microsoft.Graph;
using Microsoft.Graph.Authentication;
using Microsoft.Graph.Models;
using Microsoft.Identity.Client;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Microsoft Graph mail provider. The Graph client is always created through the immutable-ID
/// pipeline; no request may use a bare client (§2).
/// </summary>
public sealed partial class GraphMailProvider(GraphOAuthAuthenticator oauth) : IMailProvider
{
	public ProviderType Type => ProviderType.Microsoft365;

	public ProviderCapabilities Capabilities { get; } = new()
	{
		Type = ProviderType.Microsoft365,
		ChangeStreamScope = ChangeStreamScope.Mailbox,
		ImapTier = ImapCapabilityTier.NotApplicable,
		ReportsMailboxCounts = true,
		ReportsDestinationIdOnMove = true,
		SupportsIncrementalFlagChanges = true,
		ReportsExpungesIncrementally = true,
		AdvancesCursorMidWalk = false,
		SupportsMultipleMailboxMembership = false,
		SupportsServerSideDrafts = true,
		DeletingMailboxDeletesMessages = true,
	};

	public Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct) =>
		oauth.AuthenticateAsync(account, ct);

	public async Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(
		Account account,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var folders = await ThrottleAwareAsync(
			() => client.Me.MailFolders.GetAsync(
				configuration =>
				{
					configuration.QueryParameters.Select =
					[
						"id",
						"displayName",
						"parentFolderId",
						"totalItemCount",
						"unreadItemCount",
					];
				},
				ct
			)
		);
		var specialUses = await SpecialUsesAsync(client, ct);

		return
		[
			.. (folders?.Value ?? [])
				.Where(folder => folder.Id is not null && folder.DisplayName is not null)
				.Select(folder => new MailboxDto
				{
					ProviderMailboxId = folder.Id!,
					Name = folder.DisplayName!,
					ParentProviderMailboxId = folder.ParentFolderId,
					SpecialUse = specialUses.GetValueOrDefault(folder.Id!, SpecialUse.None),
					IsSubscribed = true,
					TotalCount = folder.TotalItemCount,
					UnreadCount = folder.UnreadItemCount,
				}),
		];
	}

	public async Task<int> EstimateMailboxCountAsync(
		Account account,
		DomainMailbox mailbox,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var folder = await ThrottleAwareAsync(
			() => client.Me.MailFolders[ProviderMailboxId(mailbox)].GetAsync(
				configuration => configuration.QueryParameters.Select = ["totalItemCount"],
				ct
			)
		);
		return folder?.TotalItemCount ?? 0;
	}

	public async Task<InitialSyncPage> InitialSyncMailboxAsync(
		Account account,
		DomainMailbox mailbox,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var messages = client.Me.MailFolders[ProviderMailboxId(mailbox)].Messages;
		var page = resumeToken is null
			? await ThrottleAwareAsync(
				() => messages.GetAsync(
					configuration =>
					{
						configuration.QueryParameters.Top = Math.Min(pageSize, bound ?? pageSize);
						configuration.QueryParameters.Orderby = ["receivedDateTime desc"];
						configuration.QueryParameters.Select = MessageSelect;
					},
					ct
				)
			)
			: await ThrottleAwareAsync(() => messages.WithUrl(resumeToken).GetAsync(null, ct));

		return new InitialSyncPage(
			[.. (page?.Value ?? []).Where(message => message.Id is not null).Select(ToDto)],
			page?.OdataNextLink,
			page?.OdataNextLink is not null,
			null
		);
	}

	public async Task<SyncResult> SyncMailboxAsync(
		Account account,
		DomainMailbox mailbox,
		ProviderCursorState? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		if (cursor is not null && cursor is not GraphDeltaCursor)
		{
			throw new ArgumentException("Graph sync requires a Graph delta cursor.", nameof(cursor));
		}

		var client = await ClientAsync(account, ct);
		var delta = client.Me.MailFolders[ProviderMailboxId(mailbox)].Messages.Delta;
		var url = continuation ?? (cursor as GraphDeltaCursor)?.DeltaLink;

		try
		{
			var page = url is null
				? await ThrottleAwareAsync(
					() => delta.GetAsDeltaGetResponseAsync(
						configuration => configuration.QueryParameters.Select = MessageSelect,
						ct
					)
				)
				: await ThrottleAwareAsync(() => delta.WithUrl(url).GetAsDeltaGetResponseAsync(null, ct));
			IReadOnlyList<GraphMessage> values = page?.Value ?? [];
			IReadOnlyList<OccurrenceRemoval> removed =
			[
				.. values
					.Where(message => message.Id is not null && message.AdditionalData?.ContainsKey("@removed") == true)
					.Select(message => new OccurrenceRemoval(ProviderMailboxId(mailbox), message.Id!)),
			];
			IReadOnlyList<MessageDto> upserted =
			[
				.. values
					.Where(message => message.Id is not null && message.AdditionalData?.ContainsKey("@removed") != true)
					.Select(ToDto),
			];

			return new SyncResult(
				page?.OdataNextLink is null && page?.OdataDeltaLink is not null
					? new GraphDeltaCursor(page.OdataDeltaLink)
					: null,
				page?.OdataNextLink,
				upserted,
				[],
				removed
			);
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex)
			when (url is not null && ex.ResponseStatusCode is 400 or 410)
		{
			throw new ProviderCursorInvalidException("Graph delta cursor has been invalidated.", ex);
		}
	}

	public Task<MailboxIntegritySnapshot> GetMailboxIntegritySnapshotAsync(
		Account account,
		DomainMailbox mailbox,
		IReadOnlyList<MessageOccurrenceRef> knownOccurrences,
		CancellationToken ct
	) => throw new NotSupportedException("Graph delta reports removals and does not require periodic mailbox integrity reconciliation.");

	private async Task<GraphServiceClient> ClientAsync(Account account, CancellationToken ct)
	{
		try
		{
			await oauth.AcquireTokenAsync(account, ct);
			var credential = new GraphAccountTokenCredential(oauth, account);
			var authenticationProvider = CreateAuthenticationProvider(credential);
			var http = GraphClientFactory.Create(
				authenticationProvider,
				[new GraphImmutableIdHandler()]
			);
			return new GraphServiceClient(http, authenticationProvider);
		}
		// Same gap pass 196/197 fixed for IMAP/SMTP, and this pass just fixed for Gmail:
		// AcquireTokenAsync throws MsalUiRequiredException when no cached account exists or a
		// silent token refresh needs interactive consent (a revoked/expired refresh token), but
		// only GraphOAuthAuthenticator.AuthenticateAsync (the initial account-setup path)
		// translated an MsalException into a categorised result. Every other Graph operation —
		// sync, mailboxes, send, drafts, mutations — funnels through this one method, so the raw
		// exception propagated uncaught past SyncJobs.GuardAsync's specific
		// ProviderAuthenticationException catch, landing in its generic fallback instead and
		// silently stopping the poll loop rather than setting AuthState.NeedsReauth (§3, §7).
		//
		// Excludes the admin-consent-required shape deliberately: AuthenticateAsync's own
		// interactive-flow catch treats that as Validation/Error, not an authentication
		// failure, precisely because re-authenticating can't fix a missing admin grant (§5).
		// Translating it here too would send a background sync failure to NeedsReauth, the
		// exact wrong outcome that carve-out exists to prevent — so it is left untranslated
		// and falls through to the same generic fallback it always did.
		catch (MsalException ex) when (!GraphOAuthAuthenticator.IsAdminConsentRequired(ex))
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}
	}

	internal static AzureIdentityAuthenticationProvider CreateAuthenticationProvider(
		Azure.Core.TokenCredential credential
	) =>
		new(
			credential,
			allowedHosts: ["graph.microsoft.com"],
			isCaeEnabled: false,
			scopes: [.. GraphOAuthAuthenticator.Scopes]
		);

	private static readonly string[] MessageSelect =
	[
		"id",
		"parentFolderId",
		"internetMessageId",
		"inReplyTo",
		"conversationId",
		"from",
		"toRecipients",
		"ccRecipients",
		"bccRecipients",
		"subject",
		"bodyPreview",
		"receivedDateTime",
		"isRead",
		"isDraft",
		"hasAttachments",
		"flag",
		"internetMessageHeaders",
	];

	internal static MessageDto ToDto(GraphMessage message)
	{
		var headers = (message.InternetMessageHeaders ?? [])
			.Where(header => header.Name is not null)
			.GroupBy(header => header.Name!, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

		return new MessageDto
		{
			ProviderStableId = message.Id,
			ProviderRevision = message.AdditionalData?.TryGetValue("@odata.etag", out var etag) == true
				? etag as string
				: null,
			Occurrences =
			[
				new MessageOccurrenceDto(message.ParentFolderId ?? string.Empty, message.Id!),
			],
			MessageIdHeader = message.InternetMessageId,
			InReplyToHeader = Header(headers, "In-Reply-To"),
			ReferencesHeader = Header(headers, "References"),
			ReplyToAddresses = Addresses(message.ReplyTo),
			SenderAddress = AddressOf(message.Sender),
			ThreadId = message.ConversationId,
			From = Addresses(message.From is null ? null : [message.From]),
			To = Addresses(message.ToRecipients),
			Cc = Addresses(message.CcRecipients),
			Bcc = Addresses(message.BccRecipients),
			Subject = message.Subject ?? string.Empty,
			Snippet = message.BodyPreview ?? string.Empty,
			ReceivedAt = message.ReceivedDateTime ?? DateTimeOffset.MinValue,
			IsRead = message.IsRead ?? false,
			IsFlagged = message.Flag?.FlagStatus == FollowupFlagStatus.Flagged,
			IsDraft = message.IsDraft ?? false,
			IsAnswered = false,
			// Graph's list/delta shape gives only a coarse HasAttachments bit. It cannot
			// establish the non-inline state exposed by the local MIME-derived model.
			HasNonInlineAttachments = null,
			SizeEstimate = null,
		};
	}

	private static IReadOnlyList<Address> Addresses(IEnumerable<Recipient>? recipients) =>
		[..
			recipients
				?.Select(AddressOf)
				.Where(address => address is not null)
				.Cast<Address>()
				?? []
		];

	private static Address? AddressOf(Recipient? recipient) =>
		recipient?.EmailAddress?.Address is string email
			? new Address(recipient.EmailAddress.Name, email)
			: null;

	private static string? Header(IReadOnlyDictionary<string, string?> headers, string name) =>
		headers.TryGetValue(name, out var value) ? value : null;

	private static string ProviderMailboxId(DomainMailbox mailbox) =>
		mailbox.ProviderMailboxId
		?? throw new InvalidOperationException("Graph mailboxes always have a provider id.");

	private static async Task<IReadOnlyDictionary<string, SpecialUse>> SpecialUsesAsync(
		GraphServiceClient client,
		CancellationToken ct
	)
	{
		var known = new (string Id, SpecialUse Use)[]
		{
			("inbox", SpecialUse.Inbox),
			("sentitems", SpecialUse.Sent),
			("drafts", SpecialUse.Drafts),
			("deleteditems", SpecialUse.Trash),
			("junkemail", SpecialUse.Junk),
			("archive", SpecialUse.Archive),
		};
		var resolved = new Dictionary<string, SpecialUse>();

		foreach (var (id, specialUse) in known)
		{
			try
			{
				var folder = await ThrottleAwareAsync(() => client.Me.MailFolders[id].GetAsync(null, ct));
				if (folder?.Id is not null)
				{
					resolved[folder.Id] = specialUse;
				}
			}
			catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				// Archive is absent in some mailboxes. A missing special folder is not an error.
			}
		}

		return resolved;
	}
}
