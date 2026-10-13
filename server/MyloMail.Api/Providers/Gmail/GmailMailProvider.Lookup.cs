using Google;
using Google.Apis.Gmail.v1;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Gmail;

public sealed partial class GmailMailProvider
{
	/// <summary>
	/// A Gmail message keeps its id for life, whatever its labels, so recovery reads it by that id: one
	/// call per id, and a 404 is the answer "deleted". The <c>Message-ID</c> search only covers a
	/// message whose id was never recorded.
	/// </summary>
	public async Task<IReadOnlyList<MessageDto>> FindMessageAsync(
		Account account,
		MessageLookup lookup,
		CancellationToken ct
	)
	{
		return await FindMessageAsync(await ServiceAsync(account, ct), lookup, ct);
	}

	internal static async Task<IReadOnlyList<MessageDto>> FindMessageAsync(
		GmailService service,
		MessageLookup lookup,
		CancellationToken ct
	)
	{
		var ids = new List<string>();
		if (lookup.ProviderStableId is not null)
		{
			ids.Add(lookup.ProviderStableId);
		}

		// Gmail's occurrence id is the message id, shared by every label.
		ids.AddRange(lookup.KnownOccurrences.Select(known => known.ProviderOccurrenceId));
		ids = [.. ids.Distinct(StringComparer.Ordinal)];

		if (ids.Count == 0 && lookup.MessageIdHeader is not null)
		{
			var search = service.Users.Messages.List(UserId);
			search.IncludeSpamTrash = true;
			search.Q = $"rfc822msgid:{lookup.MessageIdHeader.Trim('<', '>')}";
			search.MaxResults = 10;
			var page = await search.ExecuteThrottleAwareAsync(ct);
			ids = [.. (page.Messages ?? []).Select(message => message.Id).OfType<string>()];
		}

		return await MessagesAsync(
			service,
			ids.Select(id => new Google.Apis.Gmail.v1.Data.Message { Id = id }),
			ct,
			skipMissing: true
		);
	}
}
