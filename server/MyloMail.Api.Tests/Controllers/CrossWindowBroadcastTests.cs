using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Controllers;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Controllers;

/// <summary>
/// §13 Epic 10: "All actions reflected live across all open windows." Thirty-third
/// architecture-review pass, resolving a lead left open by passes 30-32: the app-wide shell
/// settings (theme, close behaviour, mailto-prompt dismissal) and the remote-content allow
/// list are a single shared row/table, not per-window state, so a change in one window must
/// broadcast — unlike panel layout/window bounds, which `AppSettingsController`'s own doc
/// comment establishes as a deliberate read-once-at-open default, not something every window
/// converges on.
/// </summary>
public sealed class CrossWindowBroadcastTests
{
	[Fact]
	public async Task Changing_the_theme_broadcasts_ShellSettingsChanged()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var controller = new AppSettingsController(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), events);

		await controller.PutTheme(new UpdateThemeRequest(ThemePreference.Dark), default);

		Assert.Equal(1, events.ShellSettingsChanged);
	}

	[Fact]
	public async Task Changing_the_close_behaviour_broadcasts_ShellSettingsChanged()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var controller = new AppSettingsController(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), events);

		await controller.PutCloseBehavior(new UpdateCloseBehaviorRequest(CloseBehavior.MinimizeToTray), default);

		Assert.Equal(1, events.ShellSettingsChanged);
	}

	/// <summary>
	/// The one deliberate carve-out: a window's own layout is read once at open and written
	/// back as the next window's default, never live-synced (§12).
	/// </summary>
	[Fact]
	public async Task Changing_the_panel_layout_does_not_broadcast()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var controller = new AppSettingsController(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), events);

		await controller.PutPanelLayout(new UpdatePanelLayoutRequest("{}"), default);

		Assert.Equal(0, events.ShellSettingsChanged);
	}

	[Fact]
	public async Task Putting_a_remote_content_rule_broadcasts_RulesChanged()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var controller = new RemoteContentController(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), events);

		await controller.Put(
			new PutRemoteContentRuleRequest(
				RemoteContentRuleScope.Domain,
				RemoteContentRuleDecision.Block,
				"@Example.ORG."
			),
			default
		);

		Assert.Equal(1, events.RemoteContentRulesChanged);
		var rule = Assert.Single(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>().RemoteContentRules);
		Assert.Equal("example.org", rule.Value);
	}

	[Fact]
	public async Task Putting_an_identical_remote_content_rule_does_not_broadcast_again()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var controller = new RemoteContentController(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(), events);
		var request = new PutRemoteContentRuleRequest(
			RemoteContentRuleScope.Sender,
			RemoteContentRuleDecision.Allow,
			"someone@example.org"
		);

		await controller.Put(request, default);
		await controller.Put(request, default);

		Assert.Equal(1, events.RemoteContentRulesChanged);
	}

	[Fact]
	public async Task Deleting_a_remote_content_rule_broadcasts_RulesChanged()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var controller = new RemoteContentController(context, events);
		await controller.Put(
			new PutRemoteContentRuleRequest(
				RemoteContentRuleScope.Sender,
				RemoteContentRuleDecision.Allow,
				"someone@example.org"
			),
			default
		);
		var rule = Assert.Single(context.RemoteContentRules);

		await controller.Delete(rule.Id, default);
		await controller.Delete(rule.Id, default);

		Assert.Equal(2, events.RemoteContentRulesChanged);
	}

	[Fact]
	public async Task Invalid_remote_content_rule_values_are_rejected_without_a_write()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var events = new RecordingEvents();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var controller = new RemoteContentController(context, events);

		var result = await controller.Put(
			new PutRemoteContentRuleRequest(
				(RemoteContentRuleScope)99,
				RemoteContentRuleDecision.Allow,
				"example.org"
			),
			default
		);

		Assert.IsType<BadRequestResult>(result);
		Assert.Empty(context.RemoteContentRules);
		Assert.Equal(0, events.RemoteContentRulesChanged);
	}

	private sealed class RecordingEvents : IHubEvents
	{
		public int ShellSettingsChanged { get; private set; }
		public int RemoteContentRulesChanged { get; private set; }

		public Task ShellSettingsChangedAsync()
		{
			ShellSettingsChanged++;
			return Task.CompletedTask;
		}

		public Task RemoteContentRulesChangedAsync()
		{
			RemoteContentRulesChanged++;
			return Task.CompletedTask;
		}

		public Task SyncProgressAsync(SyncProgressDto progress) => Task.CompletedTask;
		public Task MailboxUpdatedAsync(MailboxSummaryDto mailbox) => Task.CompletedTask;
		public Task MailboxTreeChangedAsync(Guid accountId) => Task.CompletedTask;
		public Task OutboxStatusChangedAsync(OutboxItemDto item) => Task.CompletedTask;
		public Task MessageSyncFailedAsync(MutationFailureDto failure) => Task.CompletedTask;
		public Task MessageReceivedAsync(MessageSummaryDto message) => Task.CompletedTask;
		public Task MessageUpdatedAsync(MessageSummaryDto message) => Task.CompletedTask;
		public Task MessageDeletedAsync(Guid messageId) => Task.CompletedTask;
		public Task DraftUpdatedAsync(Guid draftId) => Task.CompletedTask;
		public Task CalendarEventUpdatedAsync(Guid eventId) => Task.CompletedTask;
		public Task CalendarConflictDetectedAsync(Guid eventId) => Task.CompletedTask;
		public Task ContactsChangedAsync(Guid accountId) => Task.CompletedTask;
		public Task AccountStatusChangedAsync(AccountDto account) => Task.CompletedTask;
		public Task NotificationReadyAsync(NotificationDto notification) => Task.CompletedTask;
		public Task ExportProgressAsync(Guid exportId, int written, int total) => Task.CompletedTask;
		public Task ConnectivityChangedAsync(bool online) => Task.CompletedTask;
	}
}
