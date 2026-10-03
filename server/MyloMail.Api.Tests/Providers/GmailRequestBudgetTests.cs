using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Http;
using Google.Apis.Requests;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Gmail;
using Xunit;
using GmailDraft = Google.Apis.Gmail.v1.Data.Draft;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// The per-account Gmail quota limiter (§15): a token bucket on a fake clock, and the proof that
/// every request the provider can make is charged to it. The bucket is what keeps an account
/// under Google's 15,000 units/min; the reactive 403/429 translation stays as the backstop.
/// </summary>
public sealed class GmailRequestBudgetTests
{
	// 600 units/min is 10 units/second, which keeps the arithmetic in these tests legible.
	private static GmailRequestBudget Budget(FakeTimeProvider clock, int burst) =>
		new(clock, unitsPerMinute: 600, burstUnits: burst);

	[Fact]
	public async Task Spending_within_the_burst_is_not_delayed()
	{
		var budget = Budget(new FakeTimeProvider(), burst: 100);

		var spend = budget.AcquireAsync(Guid.NewGuid(), 100, CancellationToken.None);

		Assert.True(spend.IsCompletedSuccessfully);
		await spend;
	}

	[Fact]
	public async Task Spending_past_the_burst_waits_for_exactly_the_refill_it_needs()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 10);
		var account = Guid.NewGuid();
		await budget.AcquireAsync(account, 10, CancellationToken.None);

		// 5 units short at 10 units/second: half a second.
		var spend = budget.AcquireAsync(account, 5, CancellationToken.None);
		Assert.False(spend.IsCompleted);
		clock.Advance(TimeSpan.FromMilliseconds(499));
		Assert.False(spend.IsCompleted);

		clock.Advance(TimeSpan.FromMilliseconds(1));
		await spend.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Waiters_queue_behind_each_other_rather_than_all_waking_together()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 10);
		var account = Guid.NewGuid();
		await budget.AcquireAsync(account, 10, CancellationToken.None);

		var first = budget.AcquireAsync(account, 10, CancellationToken.None);
		var second = budget.AcquireAsync(account, 10, CancellationToken.None);

		// The first needs one second of refill, the second needs two: reserving up front is
		// what stops both being released at the one-second mark.
		clock.Advance(TimeSpan.FromSeconds(1));
		await first.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(second.IsCompleted);
		clock.Advance(TimeSpan.FromSeconds(1));
		await second.WaitAsync(TimeSpan.FromSeconds(5));
	}

	/// <summary>
	/// A long idle must not bank more than the burst, or the "burst + a minute of refill stays
	/// under the quota" bound no longer holds.
	/// </summary>
	[Fact]
	public async Task Refill_never_banks_more_than_the_burst()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 10);
		var account = Guid.NewGuid();
		await budget.AcquireAsync(account, 10, CancellationToken.None);
		clock.Advance(TimeSpan.FromHours(1));

		await budget.AcquireAsync(account, 10, CancellationToken.None);
		var overdraft = budget.AcquireAsync(account, 10, CancellationToken.None);

		Assert.False(overdraft.IsCompleted);
		clock.Advance(TimeSpan.FromSeconds(1));
		await overdraft.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Each_account_has_its_own_bucket()
	{
		var budget = Budget(new FakeTimeProvider(), burst: 10);
		await budget.AcquireAsync(Guid.NewGuid(), 10, CancellationToken.None);

		var other = budget.AcquireAsync(Guid.NewGuid(), 10, CancellationToken.None);

		Assert.True(other.IsCompletedSuccessfully);
	}

	/// <summary>
	/// Cancelling a wait must both stop waiting and give the reservation back. Without the
	/// refund, an abandoned job would leave a debt that delays every later request of the
	/// account, which the cancelled caller never benefited from.
	/// </summary>
	[Fact]
	public async Task A_cancelled_wait_stops_waiting_and_returns_its_units()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 10);
		var account = Guid.NewGuid();
		await budget.AcquireAsync(account, 10, CancellationToken.None);

		using var cancel = new CancellationTokenSource();
		var abandoned = budget.AcquireAsync(account, 10, cancel.Token);
		Assert.False(abandoned.IsCompleted);
		await cancel.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

		// One second of refill pays for the next request in full only if the cancelled one's
		// ten units were returned; unreturned, it would need two.
		var next = budget.AcquireAsync(account, 10, CancellationToken.None);
		clock.Advance(TimeSpan.FromSeconds(1));
		await next.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task A_token_already_cancelled_throws_without_spending()
	{
		var budget = Budget(new FakeTimeProvider(), burst: 10);
		var account = Guid.NewGuid();
		using var cancel = new CancellationTokenSource();
		await cancel.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			budget.AcquireAsync(account, 10, cancel.Token)
		);

		Assert.True(budget.AcquireAsync(account, 10, CancellationToken.None).IsCompletedSuccessfully);
	}

	/// <summary>
	/// The bound the defaults exist to keep: a bucket may hand out its burst plus a minute of
	/// refill inside one minute, and that must stay under Google's 15,000 units/min/user.
	/// </summary>
	[Fact]
	public void The_defaults_keep_a_full_minute_under_googles_quota()
	{
		Assert.True(
			GmailRequestBudget.DefaultUnitsPerMinute + GmailRequestBudget.DefaultBurstUnits < 15_000
		);
	}

	[Fact]
	public void Request_units_follow_googles_published_table()
	{
		using var service = new GmailService(
			new Google.Apis.Services.BaseClientService.Initializer { ApplicationName = "MyloMail tests" }
		);

		Assert.Equal(5, GmailQuotaUnits.For(service.Users.Messages.Get("me", "id")));
		Assert.Equal(5, GmailQuotaUnits.For(service.Users.Messages.List("me")));
		Assert.Equal(2, GmailQuotaUnits.For(service.Users.History.List("me")));
		Assert.Equal(1, GmailQuotaUnits.For(service.Users.Labels.List("me")));
		Assert.Equal(1, GmailQuotaUnits.For(service.Users.Labels.Get("me", "L")));
		Assert.Equal(1, GmailQuotaUnits.For(service.Users.GetProfile("me")));
		Assert.Equal(5, GmailQuotaUnits.For(service.Users.Messages.Trash("me", "id")));
		Assert.Equal(100, GmailQuotaUnits.For(service.Users.Messages.Send(new GmailMessage(), "me")));
		Assert.Equal(
			50,
			GmailQuotaUnits.For(service.Users.Messages.BatchModify(new BatchModifyMessagesRequest(), "me"))
		);
		Assert.Equal(10, GmailQuotaUnits.For(service.Users.Drafts.Create(new GmailDraft(), "me")));
		Assert.Equal(15, GmailQuotaUnits.For(service.Users.Drafts.Update(new GmailDraft(), "me", "d")));
	}

	[Fact]
	public void A_request_the_table_does_not_name_is_charged_the_default_rather_than_nothing()
	{
		using var service = new GmailService(
			new Google.Apis.Services.BaseClientService.Initializer { ApplicationName = "MyloMail tests" }
		);

		Assert.Equal(GmailQuotaUnits.Unlisted, GmailQuotaUnits.For(service.Users.Settings.GetImap("me")));
	}

	/// <summary>
	/// The wait happens before the request is sent, so a request the bucket cannot afford yet
	/// never reaches the network — which is the whole difference between pacing and reacting.
	/// </summary>
	[Fact]
	public async Task A_request_the_bucket_cannot_afford_waits_before_it_is_sent()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 5);
		var handler = new CountingHandler();
		using var service = MeteredService(handler, Guid.NewGuid(), budget);

		await service.Users.Messages.Get("me", "a").ExecuteThrottleAwareAsync(CancellationToken.None);
		var second = service.Users.Messages.Get("me", "b").ExecuteThrottleAwareAsync(CancellationToken.None);

		Assert.False(second.IsCompleted);
		Assert.Equal(1, handler.Requests);
		clock.Advance(TimeSpan.FromMilliseconds(500));
		await second.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(2, handler.Requests);
	}

	/// <summary>
	/// Backfill, the change stream, content downloads and mutations each build their own
	/// service, but Google counts one user. Every service the provider builds for an account
	/// must therefore spend from the same bucket, and another account's must not.
	/// </summary>
	[Fact]
	public async Task Every_service_the_provider_builds_for_an_account_shares_that_accounts_bucket()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 5);
		var provider = new GmailMailProvider(null!, null!, budget);
		var handler = new CountingHandler();
		var account = new Account { Id = Guid.NewGuid() };
		var neighbour = new Account { Id = Guid.NewGuid() };
		using var backfill = provider.BuildService(account, null, Factory(handler));
		using var content = provider.BuildService(account, null, Factory(handler));
		using var unrelated = provider.BuildService(neighbour, null, Factory(handler));

		await backfill.Users.Messages.Get("me", "a").ExecuteThrottleAwareAsync(CancellationToken.None);
		var sameAccount = content.Users.Messages.Get("me", "b").ExecuteThrottleAwareAsync(CancellationToken.None);
		var otherAccount = unrelated.Users.Messages.Get("me", "c").ExecuteThrottleAwareAsync(CancellationToken.None);

		Assert.False(sameAccount.IsCompleted);
		await otherAccount.WaitAsync(TimeSpan.FromSeconds(5));
		clock.Advance(TimeSpan.FromMilliseconds(500));
		await sameAccount.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task A_service_with_no_meter_is_not_charged()
	{
		var handler = new CountingHandler();
		using var service = new GmailService(
			new Google.Apis.Services.BaseClientService.Initializer
			{
				ApplicationName = "MyloMail tests",
				HttpClientFactory = Factory(handler),
			}
		);
		service.AttachThrottleTracker(new GmailThrottleTracker());

		for (var request = 0; request < 3; request++)
		{
			await service.Users.Messages.Get("me", "x").ExecuteThrottleAwareAsync(CancellationToken.None);
		}

		Assert.Equal(3, handler.Requests);
	}

	/// <summary>
	/// A batch is billed as the sum of its calls: two 5-unit calls against a bucket holding 6.
	/// </summary>
	[Fact]
	public async Task A_batch_is_charged_the_sum_of_its_calls_before_it_is_sent()
	{
		var clock = new FakeTimeProvider();
		var budget = Budget(clock, burst: 6);
		var handler = new CountingHandler(batch: true);
		using var service = MeteredService(handler, Guid.NewGuid(), budget);
		var batch = new BatchRequest(service);
		batch.QueueMetered<GmailMessage>(service.Users.Messages.Trash("me", "a"), (_, _, _, _) => { });
		batch.QueueMetered<GmailMessage>(service.Users.Messages.Trash("me", "b"), (_, _, _, _) => { });

		var send = batch.ExecuteThrottleAwareAsync(service, CancellationToken.None);

		// 10 units against 6 available is 4 short: 0.4 seconds, and nothing on the wire yet.
		Assert.False(send.IsCompleted);
		Assert.Equal(0, handler.Requests);
		clock.Advance(TimeSpan.FromMilliseconds(400));
		await send.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(1, handler.Requests);
	}

	private static GmailService MeteredService(HttpMessageHandler handler, Guid account, GmailRequestBudget budget)
	{
		var service = new GmailService(
			new Google.Apis.Services.BaseClientService.Initializer
			{
				ApplicationName = "MyloMail tests",
				HttpClientFactory = Factory(handler),
			}
		);
		service.AttachThrottleTracker(new GmailThrottleTracker());
		service.AttachQuotaMeter(account, budget);
		return service;
	}

	private static Google.Apis.Http.IHttpClientFactory Factory(HttpMessageHandler handler) =>
		new HttpClientFromMessageHandlerFactory(_ =>
			new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(handler, false, false)
		);

	private sealed class CountingHandler(bool batch = false) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			Requests++;
			if (!batch)
			{
				return Task.FromResult(
					new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent("{\"id\":\"x\"}", Encoding.UTF8, "application/json"),
					}
				);
			}

			const string boundary = "batch_response";
			var body = string.Join(
				"\r\n",
				$"--{boundary}",
				"Content-Type: application/http",
				"Content-ID: response-1",
				"",
				"HTTP/1.1 200 OK",
				"Content-Type: application/json",
				"",
				"{\"id\":\"a\"}",
				$"--{boundary}",
				"Content-Type: application/http",
				"Content-ID: response-2",
				"",
				"HTTP/1.1 200 OK",
				"Content-Type: application/json",
				"",
				"{\"id\":\"b\"}",
				$"--{boundary}--",
				""
			);
			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8),
			};
			response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
				$"multipart/mixed; boundary={boundary}"
			);
			return Task.FromResult(response);
		}
	}
}
