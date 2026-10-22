using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// A Gmail poll made while the account is still being walked is staged and replayed afterwards.
/// A poll that found nothing leaves no event, but anything it did find must always be staged:
/// skipping a page that held a change is silent data loss (§3).
/// </summary>
public sealed class StagedPageTests
{
	private static readonly MessageDto AMessage = new() { ProviderStableId = "m1", Occurrences = [], ReceivedAt = DateTimeOffset.UnixEpoch };

	public static TheoryData<string> EveryKindOfChange() =>
		["a message", "a flag change", "a removal", "a permanent deletion", "a remote draft"];

	private static (SyncResult Result, IReadOnlyList<RemoteDraftPayload> Drafts) PageWith(string kind) =>
		kind switch
		{
			"a message" => (new SyncResult(null, null, [AMessage], [], []), []),
			"a flag change" => (new SyncResult(null, null, [], [new OccurrenceFlagChange("INBOX", "1", true, null)], []), []),
			"a removal" => (new SyncResult(null, null, [], [], [new OccurrenceRemoval("INBOX", "1")]), []),
			"a permanent deletion" => (new SyncResult(null, null, [], [], [], ["m1"]), []),
			_ => (new SyncResult(null, null, [], [], []), [new RemoteDraftPayload("Drafts", "d1", "m1", null, DateTimeOffset.UnixEpoch, [])]),
		};

	[Theory]
	[MemberData(nameof(EveryKindOfChange))]
	public void A_page_with_any_kind_of_change_is_staged(string kind)
	{
		var (result, drafts) = PageWith(kind);

		Assert.True(ChangeStreamService.CarriesChanges(result, drafts));
	}

	[Fact]
	public void A_page_that_found_nothing_is_not()
	{
		Assert.False(ChangeStreamService.CarriesChanges(new SyncResult(null, null, [], [], []), []));
		Assert.False(ChangeStreamService.CarriesChanges(new SyncResult(null, null, [], [], [], []), []));
	}

	[Fact]
	public async Task Polls_that_find_nothing_leave_no_event_behind_while_a_poll_that_finds_mail_does()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		var afterBaseline = await StagedCountAsync(harness);

		// Nothing changed at the provider: three polls, three cursor advances, no events.
		for (var poll = 0; poll < 3; poll++)
		{
			await SyncTests.SyncAsync(harness);
		}
		Assert.Equal(afterBaseline, await StagedCountAsync(harness));
		Assert.NotNull(await CursorAsync(harness));

		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.SyncAsync(harness);

		Assert.Equal(afterBaseline + 1, await StagedCountAsync(harness));
	}

	private static Task<int> StagedCountAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope => await scope.GetRequiredService<MyloMailDbContext>().StagedChangeEvents.CountAsync());

	private static Task<ProviderCursorState?> CursorAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().ChangeStreamStates.SingleAsync()).CursorState);
}
