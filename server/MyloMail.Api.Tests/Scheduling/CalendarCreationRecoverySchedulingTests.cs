using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

public sealed class CalendarCreationRecoverySchedulingTests
{
	[Fact]
	public async Task Startup_requeues_an_ambiguous_calendar_create_while_periodic_polling_is_paused()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(row => row.Id == harness.Account.Id);
			account.PollingEnabled = false;
			var calendar = new Calendar
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderCalendarId = "calendar",
				Name = "Calendar",
			};
			context.Calendars.Add(calendar);
			context.CalendarCreationAttempts.Add(
				new CalendarCreationAttempt
				{
					Id = Guid.NewGuid(),
					CalendarId = calendar.Id,
					ProviderCreationKey = "mcreate",
					ICalUid = "create",
					Title = "Ambiguous create",
					DispatchedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async scope => await scope.GetRequiredService<StartupScheduler>().ScheduleAsync());

		var created = await harness.UsingAsync(scope =>
			Task.FromResult(((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created)
		);
		Assert.Contains(created, job => job.Method.Name == nameof(SyncJobs.CalendarCreationRecoveryAsync));
	}
}
