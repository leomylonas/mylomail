using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers;

public sealed class LocalContactProvider : IContactProvider
{
	private static InvalidOperationException Remote() => new("IMAP contacts are local-only.");
	public Task<ContactPullResult> PullAsync(Account account, bool useCursor, CancellationToken ct) =>
		Task.FromResult(new ContactPullResult([], [], null, true));
	public Task<ProviderContact> CreateAsync(Account account, ProviderContactWrite contact, CancellationToken ct) => Task.FromException<ProviderContact>(Remote());
	public Task<ProviderContact> UpdateAsync(Account account, string providerContactId, string? providerContainerId, string? expectedRevision, ProviderContactWrite contact, CancellationToken ct) => Task.FromException<ProviderContact>(Remote());
	public Task DeleteAsync(Account account, string providerContactId, string? providerContainerId, string? expectedRevision, CancellationToken ct) => Task.FromException(Remote());
}

public sealed class GooglePeopleContactProvider(GmailOAuthAuthenticator oauth, HttpClient client) : IContactProvider
{
	private const string BaseUrl = "https://people.googleapis.com/v1";

	public async Task<ContactPullResult> PullAsync(
		Account account,
		bool useCursor,
		CancellationToken ct
	)
	{
		var cursor = useCursor ? account.ContactSyncCursor : null;
		try
		{
			return await PullAsync(account, cursor, ct);
		}
		catch (ExpiredGoogleSyncTokenException) when (cursor is not null)
		{
			return await PullAsync(account, null, ct);
		}
	}

	private async Task<ContactPullResult> PullAsync(
		Account account,
		string? cursor,
		CancellationToken ct
	)
	{
		var resourceNames = new List<string>();
		var deletedResourceNames = new HashSet<string>(StringComparer.Ordinal);
		var previousByResourceName = new Dictionary<string, IReadOnlyList<string>>(
			StringComparer.Ordinal
		);
		var nextCursor = cursor;
		string? pageToken = null;
		do
		{
			var url = $"{BaseUrl}/people/me/connections?personFields=metadata&pageSize=1000"
				+ "&sources=READ_SOURCE_TYPE_CONTACT";
			url += cursor is null
				? "&requestSyncToken=true"
				: "&syncToken=" + Uri.EscapeDataString(cursor);
			if (pageToken is not null) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
			using var request = await RequestAsync(account, HttpMethod.Get, url, ct);
			using var response = await client.SendAsync(request, ct);
			if (response.StatusCode == HttpStatusCode.Gone && cursor is not null)
				throw new ExpiredGoogleSyncTokenException();
			await EnsureAsync(response, ct);
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
			if (json.RootElement.TryGetProperty("connections", out var rows))
			{
				foreach (var row in rows.EnumerateArray())
				{
					var resourceName = row.GetProperty("resourceName").GetString()!;
					var previous = PreviousResourceNames(row);
					if (IsDeleted(row))
					{
						deletedResourceNames.Add(resourceName);
						deletedResourceNames.UnionWith(previous);
						continue;
					}
					resourceNames.Add(resourceName);
					previousByResourceName[resourceName] = previous;
				}
			}
			pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next)
				? next.GetString()
				: null;
			if (json.RootElement.TryGetProperty("nextSyncToken", out var syncToken))
				nextCursor = syncToken.GetString();
		} while (!string.IsNullOrEmpty(pageToken));

		var contacts = new List<ProviderContact>();
		foreach (var batch in resourceNames.Chunk(100))
		{
			var resources = string.Join(
				"&resourceNames=",
				batch.Select(Uri.EscapeDataString)
			);
			var url = $"{BaseUrl}/people:batchGet?resourceNames={resources}"
				+ "&personFields=names,emailAddresses,metadata&sources=READ_SOURCE_TYPE_CONTACT";
			using var request = await RequestAsync(account, HttpMethod.Get, url, ct);
			using var response = await client.SendAsync(request, ct);
			await EnsureAsync(response, ct);
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
			if (!json.RootElement.TryGetProperty("responses", out var rows)) continue;
			foreach (var row in rows.EnumerateArray())
			{
				if (!row.TryGetProperty("person", out var person)) continue;
				var parsed = Parse(person);
				contacts.Add(parsed with
				{
					PreviousProviderContactIds =
						previousByResourceName.GetValueOrDefault(parsed.ProviderContactId) ?? [],
				});
			}
		}
		return new ContactPullResult(
			contacts,
			[.. deletedResourceNames],
			nextCursor,
			cursor is null
		);
	}

	public async Task<ProviderContact> CreateAsync(
		Account account,
		ProviderContactWrite contact,
		CancellationToken ct
	) => await WriteAsync(
		account,
		HttpMethod.Post,
		$"{BaseUrl}/people:createContact?personFields=names,emailAddresses,metadata&sources=READ_SOURCE_TYPE_CONTACT",
		null,
		contact,
		null,
		ct
	);

	public async Task<ProviderContact> UpdateAsync(
		Account account,
		string id,
		string? providerContainerId,
		string? expectedRevision,
		ProviderContactWrite contact,
		CancellationToken ct
	) => await WriteAsync(
		account,
		HttpMethod.Patch,
		$"{BaseUrl}/{ResourcePath(id)}:updateContact?updatePersonFields=names,emailAddresses&personFields=names,emailAddresses,metadata&sources=READ_SOURCE_TYPE_CONTACT",
		expectedRevision,
		contact,
		id,
		ct
	);

	public Task DeleteAsync(
		Account account,
		string id,
		string? providerContainerId,
		string? expectedRevision,
		CancellationToken ct
	) => Task.FromException(
		new ProviderContactRejectedException(
			"Google People does not provide an atomic revision precondition for contact deletion."
		)
	);

	private async Task<ProviderContact> WriteAsync(
		Account account,
		HttpMethod method,
		string url,
		string? revision,
		ProviderContactWrite contact,
		string? id,
		CancellationToken ct
	)
	{
		using var request = await RequestAsync(account, method, url, ct);
		if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
		var names = new[] { new { unstructuredName = contact.DisplayName } };
		var emails = contact.Emails.Select(email => new { value = email });
		request.Content = revision is null
			? JsonContent.Create(new { names, emailAddresses = emails })
			: JsonContent.Create(new
			{
				resourceName = id,
				metadata = new
				{
					sources = new[] { new { type = "CONTACT", etag = revision } },
				},
				names,
				emailAddresses = emails,
			});
		using var response = await client.SendAsync(request, ct);
		await EnsureAsync(response, ct);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
		return Parse(json.RootElement);
	}

	private async Task<HttpRequestMessage> RequestAsync(
		Account account,
		HttpMethod method,
		string url,
		CancellationToken ct
	)
	{
		try
		{
			var credential = await oauth.AuthorizeAsync(
				account,
				requireCalendarScope: false,
				requireContactsScope: true,
				ct
			);
			var token = await credential.GetAccessTokenForRequestAsync();
			return new HttpRequestMessage(method, url)
			{
				Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
			};
		}
		catch (TokenResponseException ex)
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}

	}
	private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
	{
		if (response.StatusCode == HttpStatusCode.TooManyRequests
			|| (response.StatusCode == HttpStatusCode.ServiceUnavailable
				&& response.Headers.RetryAfter is not null))
			throw new ProviderThrottledException(RetryAfter(response, TimeSpan.FromMinutes(1)), "Google People throttled contact synchronization.");
		if (response.StatusCode == HttpStatusCode.Unauthorized)
			throw new ProviderAuthenticationException("Google rejected contact authorization.");
		if (response.StatusCode is HttpStatusCode.NotFound
			or HttpStatusCode.PreconditionFailed
			or HttpStatusCode.Conflict)
			throw new ProviderConflictException("The Google contact changed remotely.");
		if ((int)response.StatusCode is >= 400 and < 500)
		{
			var body = await response.Content.ReadAsStringAsync(ct);
			if (body.Contains("failedPrecondition", StringComparison.OrdinalIgnoreCase))
				throw new ProviderConflictException("The Google contact changed remotely.");
			throw new ProviderContactRejectedException($"Google People rejected the contact operation with HTTP {(int)response.StatusCode}.");
		}
		response.EnsureSuccessStatusCode();
	}

	private static TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback)
	{
		var delay = response.Headers.RetryAfter?.Delta;
		if (delay is null && response.Headers.RetryAfter?.Date is { } date)
			delay = date - DateTimeOffset.UtcNow;
		return delay is { } value && value > TimeSpan.Zero ? value : fallback;
	}

	private static string ResourcePath(string resourceName) =>
		string.Join("/", resourceName.Split('/').Select(Uri.EscapeDataString));

	private static ProviderContact Parse(JsonElement row) => new(
		row.GetProperty("resourceName").GetString()!,
		ContactSourceRevision(row),
		row.TryGetProperty("names", out var names) && names.GetArrayLength() > 0
			? names[0].GetProperty("displayName").GetString() ?? ""
			: "",
		row.TryGetProperty("emailAddresses", out var emails)
			? emails.EnumerateArray()
				.Select(email => email.GetProperty("value").GetString())
				.Where(email => email is not null)
				.Cast<string>()
				.ToArray()
			: []
	);

	private static string? ContactSourceRevision(JsonElement person)
	{
		if (!person.TryGetProperty("metadata", out var metadata)
			|| !metadata.TryGetProperty("sources", out var sources)) return null;
		foreach (var source in sources.EnumerateArray())
		{
			if (source.TryGetProperty("type", out var type)
				&& type.GetString() == "CONTACT"
				&& source.TryGetProperty("etag", out var etag)) return etag.GetString();
		}
		return null;
	}

	private static bool IsDeleted(JsonElement person) =>
		person.TryGetProperty("metadata", out var metadata)
		&& metadata.TryGetProperty("deleted", out var deleted)
		&& deleted.GetBoolean();

	private static IReadOnlyList<string> PreviousResourceNames(JsonElement person) =>
		person.TryGetProperty("metadata", out var metadata)
		&& metadata.TryGetProperty("previousResourceNames", out var previous)
			? previous.EnumerateArray()
				.Select(resourceName => resourceName.GetString())
				.OfType<string>()
				.ToArray()
			: [];

	private sealed class ExpiredGoogleSyncTokenException : Exception;
}

public sealed class GraphContactProvider(GraphOAuthAuthenticator oauth, HttpClient client) : IContactProvider
{
	private const string MeUrl = "https://graph.microsoft.com/v1.0/me";
	private const string DefaultContactsUrl = $"{MeUrl}/contacts";
	private const string ContactFields = "id,displayName,emailAddresses,changeKey";

	public async Task<ContactPullResult> PullAsync(
		Account account,
		bool useCursor,
		CancellationToken ct
	)
	{
		var contacts = new List<ProviderContact>();
		await ReadContactsAsync(account, $"{DefaultContactsUrl}?$select={ContactFields}", null, contacts, ct);
		var folders = new Queue<(string Id, string Path)>(
			(await ReadFolderIdsAsync(account, $"{MeUrl}/contactFolders?$select=id&$top=100", ct))
				.Select(id => (id, $"contactFolders/{Uri.EscapeDataString(id)}"))
		);
		var visited = new HashSet<string>(StringComparer.Ordinal);
		while (folders.TryDequeue(out var folder))
		{
			if (!visited.Add(folder.Id)) continue;
			var folderUrl = $"{MeUrl}/{folder.Path}";
			await ReadContactsAsync(
				account,
				$"{folderUrl}/contacts?$select={ContactFields}",
				folder.Path,
				contacts,
				ct
			);
			foreach (var child in await ReadFolderIdsAsync(
				account,
				$"{folderUrl}/childFolders?$select=id&$top=100",
				ct
			))
				folders.Enqueue((
					child,
					$"{folder.Path}/childFolders/{Uri.EscapeDataString(child)}"
				));
		}
		return new ContactPullResult(ResolveContactDuplicates(contacts), [], null, true);
	}

	private static IReadOnlyList<ProviderContact> ResolveContactDuplicates(
		IReadOnlyList<ProviderContact> contacts
	)
	{
		var resolved = new List<ProviderContact>();
		foreach (var group in contacts.GroupBy(
			contact => contact.ProviderContactId,
			StringComparer.Ordinal
		))
		{
			var first = group.First();
			if (group.Skip(1).Any(contact => !SameProjection(first, contact)))
				throw new HttpRequestException(
					"Microsoft Graph returned conflicting observations for one contact."
				);
			resolved.Add(first);
		}
		return resolved;
	}

	private static bool SameProjection(ProviderContact left, ProviderContact right) =>
		left.Revision == right.Revision
		&& left.ProviderContainerId == right.ProviderContainerId
		&& left.DisplayName == right.DisplayName
		&& left.Emails.Order(StringComparer.OrdinalIgnoreCase)
			.SequenceEqual(
				right.Emails.Order(StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase
			);

	public Task<ProviderContact> CreateAsync(
		Account account,
		ProviderContactWrite contact,
		CancellationToken ct
	) => WriteAsync(account, HttpMethod.Post, DefaultContactsUrl, null, null, contact, ct);

	public Task<ProviderContact> UpdateAsync(
		Account account,
		string id,
		string? providerContainerId,
		string? expectedRevision,
		ProviderContactWrite contact,
		CancellationToken ct
	) => WriteAsync(
		account,
		HttpMethod.Patch,
		ContactUrl(id, providerContainerId),
		providerContainerId,
		expectedRevision,
		contact,
		ct
	);

	public async Task DeleteAsync(
		Account account,
		string id,
		string? providerContainerId,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		using var response = await SendAsync(
			account,
			HttpMethod.Delete,
			ContactUrl(id, providerContainerId),
			expectedRevision,
			null,
			ct
		);
		Ensure(response);
	}

	private async Task ReadContactsAsync(
		Account account,
		string initialUrl,
		string? providerContainerId,
		List<ProviderContact> contacts,
		CancellationToken ct
	)
	{
		string? url = initialUrl;
		while (url is not null)
		{
			using var response = await SendAsync(account, HttpMethod.Get, url, null, null, ct);
			Ensure(response);
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
			contacts.AddRange(json.RootElement.GetProperty("value")
				.EnumerateArray()
				.Select(row => Parse(row, providerContainerId)));
			url = json.RootElement.TryGetProperty("@odata.nextLink", out var next)
				? next.GetString()
				: null;
		}
	}

	private async Task<IReadOnlyList<string>> ReadFolderIdsAsync(
		Account account,
		string initialUrl,
		CancellationToken ct
	)
	{
		var ids = new List<string>();
		string? url = initialUrl;
		while (url is not null)
		{
			using var response = await SendAsync(account, HttpMethod.Get, url, null, null, ct);
			Ensure(response);
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
			ids.AddRange(json.RootElement.GetProperty("value").EnumerateArray()
				.Select(folder => folder.GetProperty("id").GetString())
				.OfType<string>());
			url = json.RootElement.TryGetProperty("@odata.nextLink", out var next)
				? next.GetString()
				: null;
		}
		return ids;
	}

	private async Task<ProviderContact> WriteAsync(
		Account account,
		HttpMethod method,
		string url,
		string? providerContainerId,
		string? revision,
		ProviderContactWrite contact,
		CancellationToken ct
	)
	{
		using var response = await SendAsync(account, method, url, revision, contact, ct);
		Ensure(response);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
		var parsed = Parse(json.RootElement, providerContainerId);
		return parsed with { Revision = response.Headers.ETag?.Tag ?? parsed.Revision };
	}

	private async Task<HttpResponseMessage> SendAsync(
		Account account,
		HttpMethod method,
		string url,
		string? revision,
		ProviderContactWrite? contact,
		CancellationToken ct
	)
	{
		string token;
		try
		{
			token = (await oauth.AcquireTokenAsync(account, ct)).Token;
		}
		catch (MsalException ex) when (!GraphOAuthAuthenticator.IsAdminConsentRequired(ex))
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}
		using var request = new HttpRequestMessage(method, url);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
		if (contact is not null)
			request.Content = JsonContent.Create(new
			{
				displayName = contact.DisplayName,
				emailAddresses = contact.Emails.Select(email => new
				{
					address = email,
					name = contact.DisplayName,
				}),
			});
		return await client.SendAsync(request, ct);
	}
	private static void Ensure(HttpResponseMessage response)
	{
		if (response.StatusCode == HttpStatusCode.TooManyRequests
			|| (response.StatusCode == HttpStatusCode.ServiceUnavailable
				&& response.Headers.RetryAfter is not null))
			throw new ProviderThrottledException(RetryAfter(response, TimeSpan.FromSeconds(30)), "Microsoft Graph throttled contact synchronization.");
		if (response.StatusCode == HttpStatusCode.Unauthorized)
			throw new ProviderAuthenticationException("Microsoft Graph rejected contact authorization.");
		if (response.StatusCode is HttpStatusCode.NotFound
			or HttpStatusCode.PreconditionFailed
			or HttpStatusCode.Conflict)
			throw new ProviderConflictException("The Microsoft contact changed remotely.");
		if ((int)response.StatusCode is >= 400 and < 500)
			throw new ProviderContactRejectedException($"Microsoft Graph rejected the contact operation with HTTP {(int)response.StatusCode}.");
		response.EnsureSuccessStatusCode();
	}

	private static TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback)
	{
		var delay = response.Headers.RetryAfter?.Delta;
		if (delay is null && response.Headers.RetryAfter?.Date is { } date)
			delay = date - DateTimeOffset.UtcNow;
		return delay is { } value && value > TimeSpan.Zero ? value : fallback;
	}

	private static string ContactUrl(string contactId, string? folderPath) => folderPath is null
		? $"{DefaultContactsUrl}/{Uri.EscapeDataString(contactId)}"
		: $"{MeUrl}/{folderPath}/contacts/{Uri.EscapeDataString(contactId)}";

	private static ProviderContact Parse(JsonElement row, string? providerContainerId = null) => new(
		row.GetProperty("id").GetString()!,
		row.TryGetProperty("@odata.etag", out var etag)
			? etag.GetString()
			: row.TryGetProperty("changeKey", out var key) ? key.GetString() : null,
		row.TryGetProperty("displayName", out var name) ? name.GetString() ?? "" : "",
		row.TryGetProperty("emailAddresses", out var emails)
			? emails.EnumerateArray()
				.Select(email => email.GetProperty("address").GetString())
				.OfType<string>()
				.ToArray()
			: [],
		providerContainerId
	);
}

public sealed class ContactProviderFactory(
	IOptions<ProviderClientOptions> options,
	ICredentialStore credentials,
	IHttpClientFactory clients
) : IContactProviderFactory
{
	private readonly IContactProvider local = new LocalContactProvider();

	public IContactProvider For(Account account) => account.ProviderType switch
	{
		ProviderType.Gmail => Gmail(),
		ProviderType.Microsoft365 => Graph(),
		_ => local,
	};

	private GooglePeopleContactProvider Gmail()
	{
		var gmail = options.Value.Gmail;
		if (!gmail.IsConfigured)
			throw new ProviderNotConfiguredException(
				ProviderType.Gmail,
				"Providers:Gmail:ClientId/ClientSecret"
			);
		return new GooglePeopleContactProvider(
			new GmailOAuthAuthenticator(
				credentials,
				new ClientSecrets { ClientId = gmail.ClientId, ClientSecret = gmail.ClientSecret }
			),
			clients.CreateClient("google-contacts")
		);
	}

	private GraphContactProvider Graph()
	{
		var graph = options.Value.Graph;
		if (!graph.IsConfigured)
			throw new ProviderNotConfiguredException(
				ProviderType.Microsoft365,
				"Providers:Graph:ClientId"
			);
		return new GraphContactProvider(
			new GraphOAuthAuthenticator(credentials, graph.ClientId!, graph.Authority),
			clients.CreateClient("graph-contacts")
		);
	}
}
