using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Graph;
using Xunit;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphMutationBatchTests
{
	[Fact]
	public async Task Flag_batch_translates_inner_throttles_using_the_longest_retry_after()
	{
		using var client = Client(new GraphBatchHandler(throttled: true));

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GraphMailProvider.SetFlagsBatchAsync(
				client,
				References(),
				new FlagUpdate(true, null),
				CancellationToken.None
			)
		);

		Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
	}

	[Fact]
	public async Task Move_batch_translates_inner_throttles_using_the_longest_retry_after()
	{
		using var client = Client(new GraphBatchHandler(throttled: true));
		var target = new DomainMailbox { Id = Guid.NewGuid(), ProviderMailboxId = "archive" };

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GraphMailProvider.MoveMessagesBatchAsync(
				client,
				References(),
				target,
				CancellationToken.None
			)
		);

		Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
	}

	[Fact]
	public async Task Trash_batch_translates_inner_throttles_using_the_longest_retry_after()
	{
		using var client = Client(new GraphBatchHandler(throttled: true));

		var exception = await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			GraphMailProvider.MoveToTrashBatchAsync(client, References(), CancellationToken.None)
		);

		Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
	}

	[Fact]
	public async Task Trash_move_returns_source_removal_and_resolved_destination_occurrence()
	{
		var trashId = Guid.NewGuid();
		var reference = new MessageOccurrenceRef(Guid.NewGuid(), Guid.NewGuid(), "message-one", trashId);
		using var client = Client(new GraphBatchHandler());

		var result = await GraphMailProvider.MoveToTrashBatchAsync(
			client,
			[reference],
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.True(item.Succeeded);
		Assert.Collection(
			item.OccurrenceChanges,
			change =>
			{
				Assert.Equal(reference.MailboxId, change.MailboxId);
				Assert.True(change.Removed);
			},
			change =>
			{
				Assert.Equal(trashId, change.MailboxId);
				Assert.Equal("moved-immutable-id", change.NewProviderOccurrenceId);
				Assert.False(change.Removed);
			}
		);
	}

	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	public async Task Batch_authentication_subresponses_pause_the_account(HttpStatusCode status)
	{
		using var client = Client(new GraphBatchHandler(itemStatus: status));

		await Assert.ThrowsAsync<ProviderAuthenticationException>(() =>
			GraphMailProvider.SetFlagsBatchAsync(
				client,
				References(),
				new FlagUpdate(true, null),
				CancellationToken.None
			)
		);
	}

	[Theory]
	[InlineData(HttpStatusCode.RequestTimeout)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	public async Task Indeterminate_batch_subresponses_do_not_become_terminal_item_failures(HttpStatusCode status)
	{
		using var client = Client(new GraphBatchHandler(itemStatus: status));

		await Assert.ThrowsAsync<HttpRequestException>(() =>
			GraphMailProvider.SetFlagsBatchAsync(
				client,
				References(),
				new FlagUpdate(true, null),
				CancellationToken.None
			)
		);
	}

	[Fact]
	public async Task Definite_client_rejections_remain_terminal_batch_item_failures()
	{
		var reference = References()[0];
		using var client = Client(new GraphBatchHandler(itemStatus: HttpStatusCode.BadRequest));

		var result = await GraphMailProvider.SetFlagsBatchAsync(
			client,
			[reference],
			new FlagUpdate(true, null),
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.False(item.Succeeded);
		Assert.Equal("400", item.Problem?.ProviderCode);
	}

	private static GraphServiceClient Client(HttpMessageHandler handler) =>
		new(new HttpClient(handler), new AnonymousAuthenticationProvider());

	private static MessageOccurrenceRef[] References() =>
	[
		new(Guid.NewGuid(), Guid.NewGuid(), "message-one", Guid.NewGuid()),
		new(Guid.NewGuid(), Guid.NewGuid(), "message-two", Guid.NewGuid()),
	];

	private sealed class GraphBatchHandler(
		bool throttled = false,
		HttpStatusCode? itemStatus = null
	) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			using var payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
			var requests = payload.RootElement.GetProperty("requests").EnumerateArray().ToList();
			var body = JsonSerializer.Serialize(new
			{
				responses = requests.Select((entry, index) => new
				{
					id = entry.GetProperty("id").GetString(),
					status = throttled ? 429 : itemStatus is { } status ? (int)status : 201,
					headers = throttled
						? new Dictionary<string, string> { ["Retry-After"] = index == 0 ? "5" : "60" }
						: new Dictionary<string, string>(),
					body = throttled || itemStatus is not null && (int)itemStatus.Value >= 400
						? null
						: new { id = "moved-immutable-id" },
				}),
			});
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}
}
