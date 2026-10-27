using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

public sealed class CalendarSearchTests
{
	[Fact]
	public async Task Search_finds_events_by_title_location_or_description_only_in_the_calendars_asked_about()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mine = Guid.NewGuid();
		var other = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Calendars.Add(new Calendar { Id = mine, AccountId = harness.Account.Id, ProviderCalendarId = "a", Name = "Mine" });
			context.Calendars.Add(new Calendar { Id = other, AccountId = harness.Account.Id, ProviderCalendarId = "b", Name = "Other" });
			void Add(Guid calendar, string title, string? location = null, string? description = null, DateTimeOffset? start = null) =>
				context.CalendarEvents.Add(new CalendarEvent
				{
					Id = Guid.NewGuid(),
					CalendarId = calendar,
					ProviderEventId = Guid.NewGuid().ToString(),
					ICalUid = Guid.NewGuid().ToString(),
					Title = title,
					Location = location,
					Description = description,
					Start = start ?? DateTimeOffset.UnixEpoch,
					End = (start ?? DateTimeOffset.UnixEpoch).AddHours(1),
				});
			Add(mine, "Dentist", start: DateTimeOffset.UnixEpoch.AddDays(2));
			Add(mine, "Lunch", location: "Dentist's cafe", start: DateTimeOffset.UnixEpoch.AddDays(1));
			Add(mine, "Review", description: "bring the DENTIST forms");
			Add(mine, "Unrelated");
			Add(other, "Dentist elsewhere");
			await context.SaveChangesAsync();
		});

		var found = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SearchCalendarEvents([mine], "dentist")
		);

		Assert.Equal(["Review", "Lunch", "Dentist"], found.Select(e => e.Title).ToList());
	}
}
