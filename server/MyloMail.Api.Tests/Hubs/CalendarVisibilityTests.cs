using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// §13 Epic 7: whether a calendar joins the unified view is a default each window reads once
/// when it opens, then owns. Unlike the account and mailbox state in
/// <see cref="AccountStateBroadcastTests"/> it is deliberately not announced — a broadcast would
/// move every window already open, which is exactly what the per-window rule forbids.
/// </summary>
public sealed class CalendarVisibilityTests
{
	[Fact]
	public async Task Hiding_a_calendar_is_reported_to_the_next_reader_and_announced_to_nobody()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (shownId, hiddenId) = await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var shown = NewCalendar(harness.Account.Id, "shown");
			var hidden = NewCalendar(harness.Account.Id, "hidden");
			context.Calendars.AddRange(shown, hidden);
			await context.SaveChangesAsync();
			return (shown.Id, hidden.Id);
		});
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetCalendarHidden(hiddenId, true)
		);
		var calendars = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().GetCalendars(harness.Account.Id)
		);

		Assert.True(calendars.Single(calendar => calendar.Id == hiddenId).IsHidden);
		Assert.False(calendars.Single(calendar => calendar.Id == shownId).IsHidden);
		Assert.Empty(harness.Events.CalendarCollections);
		Assert.Empty(harness.Events.AccountStatuses);
	}

	private static Calendar NewCalendar(Guid accountId, string providerCalendarId) =>
		new()
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			ProviderCalendarId = providerCalendarId,
			Name = providerCalendarId,
		};
}
