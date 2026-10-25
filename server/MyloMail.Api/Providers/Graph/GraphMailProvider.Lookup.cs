using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

public sealed partial class GraphMailProvider
{
	/// <summary>
	/// An immutable id follows a message across folders, so reading it returns wherever the message
	/// is now (<c>parentFolderId</c>) and a 404 says it is gone. A copy whose id is not known is looked
	/// up once by <c>internetMessageId</c> across the whole mailbox, not folder by folder.
	/// </summary>
	public async Task<IReadOnlyList<MessageDto>> FindMessageAsync(
		Account account,
		MessageLookup lookup,
		CancellationToken ct
	)
	{
		return await FindMessageAsync(await ClientAsync(account, ct), lookup, ct);
	}

	internal static async Task<IReadOnlyList<MessageDto>> FindMessageAsync(
		GraphServiceClient client,
		MessageLookup lookup,
		CancellationToken ct
	)
	{
		var ids = new List<string>();
		if (lookup.ProviderStableId is not null)
		{
			ids.Add(lookup.ProviderStableId);
		}

		ids.AddRange(lookup.KnownOccurrences.Select(known => known.ProviderOccurrenceId));
		var found = new List<GraphMessage>();
		foreach (var id in ids.Distinct(StringComparer.Ordinal))
		{
			try
			{
				var message = await ThrottleAwareAsync(() =>
					client.Me.Messages[id].GetAsync(
						configuration => configuration.QueryParameters.Select = MessageSelect,
						ct
					)
				);
				if (message?.Id is not null)
				{
					found.Add(message);
				}
			}
			catch (ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				// This id is gone; another may not be.
			}
		}

		if (found.Count == 0 && lookup.MessageIdHeader is not null)
		{
			var page = await ThrottleAwareAsync(() =>
				client.Me.Messages.GetAsync(
					configuration =>
					{
						configuration.QueryParameters.Filter =
							$"internetMessageId eq '{lookup.MessageIdHeader.Replace("'", "''")}'";
						configuration.QueryParameters.Select = MessageSelect;
						configuration.QueryParameters.Top = 10;
					},
					ct
				)
			);
			found.AddRange((page?.Value ?? []).Where(message => message.Id is not null));
		}

		return [.. found.DistinctBy(message => message.Id).Select(message => ToDto(message))];
	}
}
