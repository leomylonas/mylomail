using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Graph.Me.TranslateExchangeIds;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Storing a received message in a Microsoft 365 mailbox as it was.
/// </summary>
/// <remarks>
/// <para>
/// Graph has no direct way. Creating a message from MIME always yields a <i>draft</i>, and the draft
/// flag cannot be changed afterwards. What does work is the mailbox import/export API, which round-trips
/// an item as an opaque stream. So: create a draft from the MIME, export it, clear the draft flag inside
/// the stream (<see cref="GraphFastTransferStream"/>, one fixed-length value), delete the draft, and
/// import the stream into the target folder. The message keeps its <c>Message-ID</c>, which is what
/// recovery looks for after an interrupted move.
/// </para>
/// <para>
/// The import is the last irreversible step and everything before it can be repeated, so a failure
/// earlier leaves nothing behind except, at worst, a draft that is deleted on the way out. These are
/// beta endpoints, delegated for work or school accounts only, and need
/// <see cref="Credentials.GraphOAuthAuthenticator.ImportExportScope"/> consented for the app.
/// </para>
/// </remarks>
public sealed partial class GraphMailProvider : IImportAuthorization
{
	private const string GraphRoot = "https://graph.microsoft.com";

	/// <summary>A client for tests, in place of the one built from the account's credentials.</summary>
	internal Func<Account, CancellationToken, Task<GraphServiceClient>>? ImportClientFactory { get; init; }

	/// <summary>Handler for the pre-authenticated import URL, for tests.</summary>
	internal HttpMessageHandler? ImportHandler { get; init; }

	public Task EnsureImportAuthorizedAsync(Account account, CancellationToken ct) =>
		oauth.AuthorizeForImportAsync(account, ct);

	public async Task ImportMessageAsync(
		Account account,
		MyloMail.Api.Domain.Mailbox target,
		byte[] rawMime,
		bool isRead,
		bool isFlagged,
		CancellationToken ct
	)
	{
		var folderId = target.ProviderMailboxId
			?? throw new InvalidOperationException("Graph mailboxes always have a provider id.");
		var client = await ImportClientAsync(account, ct);
		var user = GraphMailbox.For(account).SharedMailbox is { } shared ? $"users/{Uri.EscapeDataString(shared)}" : "me";

		try
		{
			var mailboxId = await PrimaryMailboxIdAsync(client, user, ct);
			var restFolderId = await RestFolderIdAsync(client, folderId, ct);

			var draftId = await CreateDraftFromMimeAsync(client, user, rawMime, ct);
			byte[] stream;
			try
			{
				stream = await ExportAsync(client, mailboxId, draftId, ct);
				if (!GraphFastTransferStream.MarkAsReceived(stream, isRead))
				{
					throw new NotSupportedException(
						"Microsoft 365 exported the message in a form this app does not recognise, so it was not imported."
					);
				}
			}
			catch
			{
				await DeleteDraftQuietlyAsync(client, draftId);
				throw;
			}

			// The draft goes before the import: nothing after the import may be left to fail, and a
			// repeat after an interruption simply makes another.
			await ThrottleAwareAsync(() => client.Me.Messages[draftId].PermanentDelete.PostAsync(cancellationToken: ct));

			var itemId = await ImportStreamAsync(client, mailboxId, restFolderId, stream, ct);
			if (isFlagged && itemId is not null)
			{
				try
				{
					await client.Me.Messages[itemId].PatchAsync(
						new GraphMessage { Flag = new FollowupFlag { FlagStatus = FollowupFlagStatus.Flagged } },
						cancellationToken: ct
					);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// The message is there; only its star is missing. Not worth calling the move ambiguous.
				}
			}
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 401)
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 403)
		{
			throw new NotSupportedException(
				"Microsoft 365 refused the mailbox import. The organisation needs to allow MyloMail the MailboxItem.ImportExport permission, and the account must be a work or school account.",
				ex
			);
		}
	}

	private async Task<GraphServiceClient> ImportClientAsync(Account account, CancellationToken ct)
	{
		if (ImportClientFactory is not null)
		{
			return await ImportClientFactory(account, ct);
		}

		try
		{
			var credential = new Credentials.GraphAccountTokenCredential(oauth, account, withImportPermission: true);
			await oauth.AcquireImportTokenAsync(account, ct);
			var authenticationProvider = CreateAuthenticationProvider(
				credential,
				Credentials.GraphOAuthAuthenticator.ImportScopesFor(account)
			);
			var http = GraphClientFactory.Create(authenticationProvider, GraphMailbox.For(account).HandlersWithOrdinaryIds());
			return new GraphServiceClient(http, authenticationProvider);
		}
		catch (Microsoft.Identity.Client.MsalUiRequiredException)
		{
			// Not a sign-in failure to put the whole account into reauthentication for: the extra
			// permission was never granted (or was withdrawn).
			throw new NotSupportedException(
				"Moving mail into this account needs a permission that has not been granted. Try the move again to be asked for it."
			);
		}
		catch (Microsoft.Identity.Client.MsalException ex)
		{
			throw Credentials.GraphOAuthAuthenticator.TranslateTokenFailure(ex);
		}
	}

	private static async Task<string> PrimaryMailboxIdAsync(GraphServiceClient client, string user, CancellationToken ct)
	{
		using var json = await SendJsonAsync(client, Method.GET, $"{GraphRoot}/beta/{user}/settings/exchange", null, ct);
		var root = json.RootElement;
		if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0)
		{
			root = values[0];
		}

		return root.TryGetProperty("primaryMailboxId", out var id) && id.GetString() is { Length: > 0 } primary
			? primary
			: throw new NotSupportedException("Microsoft 365 did not say which mailbox this account is, so mail cannot be moved into it.");
	}

	/// <summary>The target folder in the ordinary id form the import API takes (ours is the immutable one).</summary>
	private static async Task<string> RestFolderIdAsync(GraphServiceClient client, string immutableFolderId, CancellationToken ct)
	{
		var translated = await ThrottleAwareAsync(() =>
			client.Me.TranslateExchangeIds.PostAsTranslateExchangeIdsPostResponseAsync(
				new TranslateExchangeIdsPostRequestBody
				{
					InputIds = [immutableFolderId],
					SourceIdType = ExchangeIdFormat.RestImmutableEntryId,
					TargetIdType = ExchangeIdFormat.RestId,
				},
				cancellationToken: ct
			)
		);
		return translated?.Value?.FirstOrDefault()?.TargetId
			?? throw new InvalidOperationException("Microsoft 365 could not translate the folder's id.");
	}

	private static async Task<string> CreateDraftFromMimeAsync(GraphServiceClient client, string user, byte[] rawMime, CancellationToken ct)
	{
		using var body = new MemoryStream(Encoding.ASCII.GetBytes(Convert.ToBase64String(rawMime)));
		var request = new RequestInformation { HttpMethod = Method.POST, URI = new Uri($"{GraphRoot}/v1.0/{user}/messages") };
		request.Headers.Clear();
		request.SetStreamContent(body, "text/plain");
		var created = await ThrottleAwareAsync(() =>
			client.RequestAdapter.SendAsync(request, GraphMessage.CreateFromDiscriminatorValue, null, ct)
		);
		return created?.Id ?? throw new InvalidOperationException("Microsoft 365 did not return the id of the message it created.");
	}

	private static async Task<byte[]> ExportAsync(GraphServiceClient client, string mailboxId, string itemId, CancellationToken ct)
	{
		var request = JsonSerializer.Serialize(new { itemIds = new[] { itemId } });
		using var json = await SendJsonAsync(
			client,
			Method.POST,
			$"{GraphRoot}/beta/admin/exchange/mailboxes/{Uri.EscapeDataString(mailboxId)}/exportItems",
			request,
			ct
		);
		var item = json.RootElement.GetProperty("value")[0];
		if (item.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } encoded)
		{
			return Convert.FromBase64String(encoded);
		}

		var detail = item.TryGetProperty("error", out var error) ? error.ToString() : "no data";
		throw new InvalidOperationException($"Microsoft 365 could not export the message ({detail}).");
	}

	private async Task<string?> ImportStreamAsync(GraphServiceClient client, string mailboxId, string folderId, byte[] stream, CancellationToken ct)
	{
		using var session = await SendJsonAsync(
			client,
			Method.POST,
			$"{GraphRoot}/beta/admin/exchange/mailboxes/{Uri.EscapeDataString(mailboxId)}/createImportSession",
			null,
			ct
		);
		var importUrl = session.RootElement.GetProperty("importUrl").GetString()
			?? throw new InvalidOperationException("Microsoft 365 did not return an import address.");

		// The import address is pre-authenticated and on another host, so it is called directly.
		using var http = ImportHandler is null ? new HttpClient() : new HttpClient(ImportHandler, disposeHandler: false);
		using var content = new StringContent(
			JsonSerializer.Serialize(new Dictionary<string, string> { ["FolderId"] = folderId, ["Mode"] = "create", ["Data"] = Convert.ToBase64String(stream) }),
			Encoding.UTF8,
			"application/json"
		);
		using var response = await http.PostAsync(importUrl, content, ct);
		if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
		{
			var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
			throw new ProviderThrottledException(wait, "Microsoft 365 throttled the mailbox import.");
		}

		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			throw new ProviderAuthenticationException("Microsoft 365 rejected the import session.");
		}

		if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
		{
			throw new NotSupportedException($"Microsoft 365 would not import the message here ({(int)response.StatusCode}).");
		}

		response.EnsureSuccessStatusCode();
		var text = await response.Content.ReadAsStringAsync(ct);
		try
		{
			using var parsed = JsonDocument.Parse(text);
			return parsed.RootElement.TryGetProperty("itemId", out var id) ? id.GetString() : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static async Task DeleteDraftQuietlyAsync(GraphServiceClient client, string draftId)
	{
		try
		{
			await client.Me.Messages[draftId].PermanentDelete.PostAsync();
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Nothing more can be done; the draft is the only thing left, and the caller is already
			// reporting why the import stopped.
		}
	}

	private static async Task<JsonDocument> SendJsonAsync(GraphServiceClient client, Method method, string url, string? body, CancellationToken ct)
	{
		var request = new RequestInformation { HttpMethod = method, URI = new Uri(url) };
		request.Headers.TryAdd("Accept", "application/json");
		if (body is not null)
		{
			request.SetStreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)), "application/json");
		}

		var stream = await ThrottleAwareAsync(() => client.RequestAdapter.SendPrimitiveAsync<Stream>(request, null, ct));
		await using var _ = stream;
		return await JsonDocument.ParseAsync(stream ?? new MemoryStream("{}"u8.ToArray()), cancellationToken: ct);
	}
}
