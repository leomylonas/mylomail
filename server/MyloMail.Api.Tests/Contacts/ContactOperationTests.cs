using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyloMail.Api.Contacts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Contacts;

public sealed class ContactOperationTests
{
	[Fact]
	public async Task Saving_a_contact_commits_intent_before_provider_execution()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada Lovelace", "ada@example.test");

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var operation = await context.ContactOperations.SingleAsync();
			Assert.Equal(contactId, operation.ContactId);
			Assert.Equal(ContactOperationKind.Create, operation.Kind);
			Assert.Equal(ContactOperationState.Pending, operation.State);
			Assert.Equal(0, harness.Provider.CreateCalls);
		});

		await ExecuteOnlyOperationAsync(harness);
		Assert.Single(harness.Provider.Contacts);
		Assert.Equal(1, harness.Provider.CreateCalls);
	}

	[Fact]
	public async Task Saving_a_contact_rejects_malformed_email_addresses()
	{
		await using var harness = await ContactHarness.CreateAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			SaveAsync(harness, null, "Invalid", "not-an-address"));

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.Contacts.ToListAsync());
			Assert.Empty(await context.ContactOperations.ToListAsync());
		});
	}

	[Fact]
	public async Task Editing_a_pending_create_replaces_it_without_turning_it_into_an_update()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await SaveAsync(harness, contactId, "Ada Lovelace", "ada@example.test");

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var operation = await context.ContactOperations.SingleAsync();
			Assert.Equal(ContactOperationKind.Create, operation.Kind);
			Assert.Equal("Ada Lovelace", operation.DisplayName);
		});
		await ExecuteOnlyOperationAsync(harness);
		Assert.Equal("Ada Lovelace", Assert.Single(harness.Provider.Contacts).DisplayName);
		Assert.Equal(1, harness.Provider.CreateCalls);
	}
	[Fact]
	public async Task Refresh_materialises_provider_contacts_and_announces_the_cache_change()
	{
		await using var harness = await ContactHarness.CreateAsync();
		harness.Provider.Seed("Grace Hopper", "grace@example.test");

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			var contact = await provider.GetRequiredService<MyloMailDbContext>()
				.Contacts.Include(c => c.Addresses).SingleAsync();
			Assert.Equal("Grace Hopper", contact.DisplayName);
			Assert.Equal("grace@example.test", Assert.Single(contact.Addresses).Email);
		});
		Assert.Contains(harness.AccountId, harness.Events.ContactAccounts);
	}

	[Fact]
	public async Task Incremental_contact_resource_rename_preserves_local_identity()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			(await context.Accounts.SingleAsync()).ProviderType = ProviderType.Gmail;
			await context.SaveChangesAsync();
		});
		harness.Provider.NextCursor = "cursor-1";
		var remote = harness.Provider.Seed("Grace Hopper", "grace@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		var contactId = await harness.UsingAsync(async provider =>
			(await provider.GetRequiredService<MyloMailDbContext>().Contacts.SingleAsync()).Id);

		harness.Provider.IsFullSnapshot = false;
		harness.Provider.NextCursor = "cursor-2";
		harness.Provider.RenameResource(remote.ProviderContactId, "people/renamed");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var contact = await context.Contacts.SingleAsync();
			Assert.Equal(contactId, contact.Id);
			Assert.Equal("people/renamed", contact.ProviderContactId);
			Assert.Equal("cursor-2", (await context.Accounts.SingleAsync()).ContactSyncCursor);
		});
	}

	[Fact]
	public async Task Incremental_contact_pull_changes_only_explicitly_deleted_contacts()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			(await context.Accounts.SingleAsync()).ProviderType = ProviderType.Gmail;
			await context.SaveChangesAsync();
		});
		harness.Provider.NextCursor = "cursor-1";
		var remote = harness.Provider.Seed("Grace Hopper", "grace@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		harness.Provider.IsFullSnapshot = false;
		harness.Provider.NextCursor = "cursor-2";
		harness.Provider.Remove(remote.ProviderContactId);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		await harness.UsingAsync(async provider =>
			Assert.Single(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default)));

		harness.Provider.DeletedProviderContactIds.Add(remote.ProviderContactId);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		await harness.UsingAsync(async provider =>
			Assert.Empty(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default)));
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Contact_cursor_never_commits_past_uncommitted_observations()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			(await context.Accounts.SingleAsync()).ProviderType = ProviderType.Gmail;
			await context.SaveChangesAsync();
		});
		harness.Provider.NextCursor = "cursor-1";
		harness.Provider.Seed("Grace Hopper", "grace@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactRefreshBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
				.RefreshAsync(harness.AccountId, default)));
		await harness.RestartAsync();

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Null((await context.Accounts.SingleAsync()).ContactSyncCursor);
			Assert.Empty(await context.Contacts.ToListAsync());
		});
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Equal("cursor-1", (await context.Accounts.SingleAsync()).ContactSyncCursor);
			Assert.Single(await context.Contacts.ToListAsync());
		});
	}
	[Fact]
	public async Task Failed_contact_refresh_scheduling_releases_its_process_claim()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await harness.UsingAsync(async provider =>
		{
			var refreshes = new ContactRefreshRegistry();
			using var connectivity = new ConnectivityMonitor(
				harness.Events,
				harness.Jobs,
				NullLogger<ConnectivityMonitor>.Instance
			);
			var jobs = new ContactJobs(
				provider.GetRequiredService<ContactService>(),
				refreshes,
				provider.GetRequiredService<AccountGate>(),
				connectivity,
				harness.Jobs
			);

			harness.Jobs.CreateFailure = new InvalidOperationException("enqueue failed");
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				jobs.StartRefreshAsync(harness.AccountId));
			Assert.True(refreshes.TryStart(harness.AccountId));
			refreshes.Stop(harness.AccountId);

			Assert.True(refreshes.TryStart(harness.AccountId));
			harness.Jobs.CreateFailure = new InvalidOperationException("schedule failed");
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				jobs.RefreshAsync(harness.AccountId, default));
			Assert.True(refreshes.TryStart(harness.AccountId));
		});
	}

	[Fact]
	public async Task Unchanged_refresh_does_not_announce_another_cache_change()
	{
		await using var harness = await ContactHarness.CreateAsync();
		harness.Provider.Seed("Grace Hopper", "grace@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		Assert.Single(harness.Events.ContactAccounts);
	}
	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Refresh_hides_one_scan_absence_without_losing_local_contact_identity()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var remote = harness.Provider.Seed("Grace Hopper", "grace@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		var contactId = await harness.UsingAsync(async provider =>
			(await provider.GetRequiredService<MyloMailDbContext>().Contacts.SingleAsync()).Id);
		harness.Provider.Revise(remote.ProviderContactId, "Grace Hopper");

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			Assert.Empty(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default));
			Assert.NotNull((await provider.GetRequiredService<MyloMailDbContext>()
				.Contacts.SingleAsync()).ProviderMissingSince);
		});
		await harness.RestartAsync();
		harness.Provider.Revise(
			remote.ProviderContactId,
			"Grace Hopper",
			"grace@example.test"
		);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
			Assert.Equal(contactId, Assert.Single(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default)).Id));
	}


	[Fact]
	public async Task Disabled_polling_stops_contact_refresh()
	{
		await using var harness = await ContactHarness.CreateAsync();
		harness.Provider.Seed("Grace Hopper", "grace@example.test");
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			account.PollingEnabled = false;
			await context.SaveChangesAsync();
		});

		var repeat = await harness.UsingAsync(provider => provider
			.GetRequiredService<ContactService>().RefreshAsync(harness.AccountId, default));

		Assert.False(repeat);
		await harness.UsingAsync(async provider =>
			Assert.Empty(await provider.GetRequiredService<MyloMailDbContext>().Contacts.ToListAsync()));
	}

	[Fact]
	public async Task Paused_account_does_not_execute_contact_operations()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			account.AuthState = AuthState.NeedsReauth;
			await context.SaveChangesAsync();
		});

		await ExecutePendingAsync(harness);

		Assert.Equal(0, harness.Provider.CreateCalls);
		await harness.UsingAsync(async provider =>
			Assert.Equal(ContactOperationState.Pending, (await provider
				.GetRequiredService<MyloMailDbContext>().ContactOperations.SingleAsync()).State));
	}
	[Fact]
	public async Task Refresh_authentication_failure_pauses_the_account_and_stops_its_loop()
	{
		await using var harness = await ContactHarness.CreateAsync();
		harness.Provider.PullFailure = new ProviderAuthenticationException("Reauthenticate.");

		var repeat = await harness.UsingAsync(provider => provider
			.GetRequiredService<ContactService>().RefreshAsync(harness.AccountId, default));

		Assert.False(repeat);
		await harness.UsingAsync(async provider =>
		{
			var account = await provider.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync();
			Assert.Equal(AuthState.NeedsReauth, account.AuthState);
			Assert.Equal("Reauthenticate.", account.LastAuthError);
		});
	}

	[Fact]
	public async Task Contact_refresh_honors_the_provider_retry_after()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var retryAfter = TimeSpan.FromSeconds(17);
		harness.Provider.PullFailure = new ProviderThrottledException(retryAfter, "Wait.");
		harness.Jobs.Created.Clear();
		harness.Jobs.States.Clear();
		var before = DateTime.UtcNow;

		await harness.UsingAsync(async provider =>
		{
			var refreshes = new ContactRefreshRegistry();
			using var connectivity = new ConnectivityMonitor(
				harness.Events,
				harness.Jobs,
				NullLogger<ConnectivityMonitor>.Instance
			);
			Assert.True(refreshes.TryStart(harness.AccountId));
			var job = new ContactJobs(
				provider.GetRequiredService<ContactService>(),
				refreshes,
				provider.GetRequiredService<AccountGate>(),
				connectivity,
				harness.Jobs
			);
			await job.RefreshAsync(harness.AccountId, default);
		});

		var scheduled = Assert.IsType<ScheduledState>(Assert.Single(harness.Jobs.States));
		Assert.InRange(
			scheduled.EnqueueAt,
			before.Add(retryAfter).AddSeconds(-1),
			DateTime.UtcNow.Add(retryAfter).AddSeconds(1)
		);
	}

	[Fact]
	public async Task Definitive_provider_rejection_is_terminal_and_edit_can_retry()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Provider.RejectWrites = true;
		await ExecutePendingAsync(harness);
		await harness.UsingAsync(async provider =>
			Assert.Equal(ContactOperationState.Rejected, (await provider
				.GetRequiredService<MyloMailDbContext>().ContactOperations.SingleAsync()).State));

		harness.Provider.RejectWrites = false;
		await SaveAsync(harness, contactId, "Ada Lovelace", "ada@example.test");
		await ExecutePendingAsync(harness);

		Assert.Equal("Ada Lovelace", Assert.Single(harness.Provider.Contacts).DisplayName);
	}

	[Fact]
	public async Task Save_uses_the_revision_observed_by_the_editor()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var observed = Assert.Single(harness.Provider.Contacts);
		harness.Provider.Revise(observed.ProviderContactId, "Remote Ada", "ada@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.SaveAsync(
				new ContactInput(
					contactId,
					harness.AccountId,
					"Local Ada",
					["ada@example.test"],
					observed.Revision
				),
				default
			));
		await ExecutePendingAsync(harness);

		Assert.Equal("Remote Ada", Assert.Single(harness.Provider.Contacts).DisplayName);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.True((await context.Contacts.SingleAsync()).SyncConflict);
			Assert.Equal(
				ContactOperationState.Conflict,
				(await context.ContactOperations.OrderByDescending(operation => operation.Sequence)
					.FirstAsync()).State
			);
		});
	}

	[Fact]
	public async Task Provider_rejection_dispatches_the_next_user_intent()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		await SaveAsync(harness, contactId, "Rejected edit", "ada@example.test");
		var rejectedId = await OperationIdAsync(harness, ContactOperationState.Pending);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var rejected = await context.ContactOperations.SingleAsync(operation => operation.Id == rejectedId);
			rejected.State = ContactOperationState.Dispatched;
			await context.SaveChangesAsync();
		});
		await SaveAsync(harness, contactId, "Later edit", "ada@example.test");
		var laterId = await OperationIdAsync(harness, ContactOperationState.Pending);

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var rejected = await context.ContactOperations.SingleAsync(operation => operation.Id == rejectedId);
			rejected.State = ContactOperationState.Pending;
			rejected.DispatchedAt = null;
			await context.SaveChangesAsync();
		});
		harness.Jobs.Created.Clear();
		harness.Provider.RejectWrites = true;

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ExecuteAsync(rejectedId, default));

		Assert.Contains(harness.Jobs.Created, job =>
			job.Method.Name == nameof(MyloMail.Api.Scheduling.ContactJobs.ExecuteAsync)
			&& Assert.IsType<Guid>(job.Args[0]) == laterId);
	}
	[Fact]
	public async Task Refresh_keeps_the_revision_paired_with_a_pending_local_projection()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var original = Assert.Single(harness.Provider.Contacts);
		await SaveAsync(harness, contactId, "Local first", "ada@example.test");
		harness.Provider.Revise(original.ProviderContactId, "Remote edit", "ada@example.test");

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			var contact = await provider.GetRequiredService<MyloMailDbContext>().Contacts.SingleAsync();
			Assert.Equal("Local first", contact.DisplayName);
			Assert.Equal(original.Revision, contact.ProviderRevision);
		});
		await SaveAsync(harness, contactId, "Local second", "ada@example.test");
		await ExecutePendingAsync(harness);
		await harness.UsingAsync(async provider =>
			Assert.True((await provider.GetRequiredService<MyloMailDbContext>()
				.Contacts.SingleAsync()).SyncConflict));
		Assert.Equal("Remote edit", Assert.Single(harness.Provider.Contacts).DisplayName);
	}

	[Fact]
	public async Task Conflict_resolution_authentication_failure_pauses_the_account()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var remote = Assert.Single(harness.Provider.Contacts);
		await SaveAsync(harness, contactId, "Local edit", "ada@example.test");
		harness.Provider.Revise(remote.ProviderContactId, "Remote edit", "ada@example.test");
		await ExecutePendingAsync(harness);
		harness.Provider.PullFailure = new ProviderAuthenticationException("Reauthenticate.");

		await Assert.ThrowsAsync<ProviderAuthenticationException>(() => harness.UsingAsync(provider =>
			provider.GetRequiredService<ContactService>()
				.ResolveConflictAsync(contactId, keepMine: true, default)));

		await harness.UsingAsync(async provider =>
			Assert.Equal(AuthState.NeedsReauth, (await provider
				.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync()).AuthState));
	}



	[Fact]
	public async Task Keep_mine_retries_a_conflicted_delete_as_a_delete()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada Lovelace", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var remote = Assert.Single(harness.Provider.Contacts);
		await DeleteAsync(harness, contactId);
		harness.Provider.Revise(remote.ProviderContactId, "Ada Byron", "ada@example.test");
		await ExecutePendingAsync(harness);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ResolveConflictAsync(contactId, keepMine: true, default));
		await ExecutePendingAsync(harness);

		Assert.Empty(harness.Provider.Contacts);
		Assert.Equal(2, harness.Provider.DeleteCalls);
	}

	[Fact]
	public async Task Keep_theirs_removes_a_conflicted_contact_that_was_deleted_remotely()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada Lovelace", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var remote = Assert.Single(harness.Provider.Contacts);
		await SaveAsync(harness, contactId, "Ada Byron", "ada@example.test");
		harness.Provider.Revise(remote.ProviderContactId, "Remote Ada", "ada@example.test");
		await ExecutePendingAsync(harness);
		harness.Provider.Remove(remote.ProviderContactId);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ResolveConflictAsync(contactId, keepMine: false, default));

		await harness.UsingAsync(async provider =>
			Assert.Empty(await provider.GetRequiredService<MyloMailDbContext>().Contacts.ToListAsync()));
	}

	[Fact]
	public async Task Delete_rejects_a_conflicted_contact_without_hiding_resolution_actions()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var remote = Assert.Single(harness.Provider.Contacts);
		await SaveAsync(harness, contactId, "Local Ada", "ada@example.test");
		harness.Provider.Revise(remote.ProviderContactId, "Remote Ada", "ada@example.test");
		await ExecutePendingAsync(harness);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			DeleteAsync(harness, contactId));

		await harness.UsingAsync(async provider =>
		{
			var contact = Assert.Single(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default));
			Assert.Equal(contactId, contact.Id);
			Assert.True(contact.SyncConflict);
			Assert.DoesNotContain(
				await provider.GetRequiredService<MyloMailDbContext>().ContactOperations.ToListAsync(),
				operation => operation.Kind == ContactOperationKind.Delete
			);
		});
	}

	[Fact]
	public async Task Google_contact_delete_is_rejected_before_it_can_hide_the_contact()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			(await context.Accounts.SingleAsync()).ProviderType = ProviderType.Gmail;
			await context.SaveChangesAsync();
		});
		harness.Provider.Seed("Ada", "ada@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		var contactId = await harness.UsingAsync(async provider =>
			(await provider.GetRequiredService<MyloMailDbContext>().Contacts.SingleAsync()).Id);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			DeleteAsync(harness, contactId));

		await harness.UsingAsync(async provider =>
			Assert.Equal(contactId, Assert.Single(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default)).Id));
	}

	[Fact]
	public async Task Unsynced_Google_contact_can_be_deleted_locally()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			(await context.Accounts.SingleAsync()).ProviderType = ProviderType.Gmail;
			await context.SaveChangesAsync();
		});
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");

		await DeleteAsync(harness, contactId);

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.Contacts.ToListAsync());
			Assert.Empty(await context.ContactOperations.ToListAsync());
		});
	}

	[Fact]
	public async Task Ambiguous_create_with_multiple_exact_matches_is_not_adopted_by_refresh()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada Lovelace", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteOnlyOperationAsync(harness));
		harness.Provider.Seed("Ada Lovelace", "ada@example.test");
		await harness.RestartAsync();

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var contact = await context.Contacts.SingleAsync(item => item.Id == contactId);
			Assert.Single(await context.Contacts.ToListAsync());
			Assert.Null(contact.ProviderContactId);
			Assert.NotEqual(
				ContactOperationState.Completed,
				(await context.ContactOperations.SingleAsync(operation => operation.ContactId == contactId)).State
			);
		});
	}
	[Fact]
	public async Task Delete_does_not_hide_a_contact_with_an_ambiguous_create()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteOnlyOperationAsync(harness));
		await harness.RestartAsync();
		var operationId = await OperationIdAsync(harness, ContactOperationState.Dispatched);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(operationId, default));
		await DeleteAsync(harness, contactId);

		await harness.UsingAsync(async provider =>
			Assert.Equal(contactId, Assert.Single(await provider.GetRequiredService<ContactService>()
				.ListAsync(harness.AccountId, null, default)).Id));
	}

	[Fact]
	public async Task Rejected_create_rewrites_a_queued_edit_as_a_new_create()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await harness.UsingAsync(async provider =>
		{
			var operation = await provider.GetRequiredService<MyloMailDbContext>()
				.ContactOperations.SingleAsync();
			operation.State = ContactOperationState.Dispatched;
			await provider.GetRequiredService<MyloMailDbContext>().SaveChangesAsync();
		});
		await SaveAsync(harness, contactId, "Ada later", "ada@example.test");
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var create = await context.ContactOperations.OrderBy(operation => operation.Sequence)
				.FirstAsync();
			create.State = ContactOperationState.Pending;
			create.DispatchedAt = null;
			await context.SaveChangesAsync();
		});
		harness.Provider.RejectWrites = true;

		await ExecutePendingAsync(harness);
		harness.Provider.RejectWrites = false;
		await ExecutePendingAsync(harness);

		Assert.Equal("Ada later", Assert.Single(harness.Provider.Contacts).DisplayName);
		await harness.UsingAsync(async provider =>
		{
			var operations = await provider.GetRequiredService<MyloMailDbContext>()
				.ContactOperations.OrderBy(operation => operation.Sequence).ToListAsync();
			Assert.Equal(ContactOperationState.Rejected, operations[0].State);
			Assert.Equal(ContactOperationKind.Create, operations[1].Kind);
			Assert.Equal(ContactOperationState.Completed, operations[1].State);
		});
	}

	[Fact]
	public async Task Contact_throttling_closes_the_account_gate_before_another_provider_call()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Provider.WriteFailure = new ProviderThrottledException(
			TimeSpan.FromMinutes(2),
			"Wait."
		);

		await ExecutePendingAsync(harness);

		await harness.UsingAsync(async provider =>
		{
			var gate = provider.GetRequiredService<AccountGate>();
			Assert.True(gate.Delay(harness.AccountId) > TimeSpan.Zero);
			await Assert.ThrowsAsync<ProviderThrottledException>(() =>
				provider.GetRequiredService<ContactService>()
					.RefreshAsync(harness.AccountId, default));
		});
		Assert.Equal(0, harness.Provider.PullCalls);
		await harness.UsingAsync(async provider =>
			Assert.Equal(ContactOperationState.Pending, (await provider
				.GetRequiredService<MyloMailDbContext>().ContactOperations.SingleAsync()).State));
	}

	[Fact]
	public async Task Credential_store_failure_keeps_contact_intent_pending_and_self_clears_after_success()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Provider.WriteFailure = new CredentialStoreUnavailableException("Unlock the keychain.");

		await ExecutePendingAsync(harness);

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(ContactOperationState.Pending, (await context.ContactOperations.SingleAsync()).State);
			Assert.Equal(AuthState.CredentialStoreUnavailable, (await context.Accounts.SingleAsync()).AuthState);
		});
		await ExecutePendingAsync(harness);
		await harness.UsingAsync(async provider =>
			Assert.Equal(AuthState.Connected, (await provider.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync()).AuthState));
	}

	[Fact]
	public async Task Ambiguous_execution_broadcasts_the_durable_state_transition()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Provider.WriteFailure = new HttpRequestException("Connection lost.");

		await Assert.ThrowsAsync<HttpRequestException>(() => ExecutePendingAsync(harness));

		Assert.Contains(harness.AccountId, harness.Events.ContactAccounts);
		await harness.UsingAsync(async provider =>
			Assert.Equal(ContactOperationState.Ambiguous, (await provider
				.GetRequiredService<MyloMailDbContext>().ContactOperations.SingleAsync()).State));
	}

	[Fact]
	public async Task Provider_timeout_after_dispatch_becomes_ambiguous_and_schedules_reconciliation()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Provider.WriteFailure = new TaskCanceledException("Provider timeout.");

		await Assert.ThrowsAsync<TaskCanceledException>(() => ExecutePendingAsync(harness));

		await harness.UsingAsync(async provider =>
			Assert.Equal(ContactOperationState.Ambiguous, (await provider
				.GetRequiredService<MyloMailDbContext>().ContactOperations.SingleAsync()).State));
		Assert.Contains(harness.Jobs.Created, job =>
			job.Method.Name == nameof(MyloMail.Api.Scheduling.ContactJobs.ReconcileAsync));
	}

	[Fact]
	public async Task Zero_address_remote_contact_is_not_mistaken_for_a_completed_ambiguous_delete()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		var remote = Assert.Single(harness.Provider.Contacts);
		await DeleteAsync(harness, contactId);
		harness.Provider.Revise(remote.ProviderContactId, "Ada");
		await harness.UsingAsync(async provider =>
		{
			var operation = await provider.GetRequiredService<MyloMailDbContext>()
				.ContactOperations.SingleAsync(candidate => candidate.Kind == ContactOperationKind.Delete);
			operation.State = ContactOperationState.Ambiguous;
			await provider.GetRequiredService<MyloMailDbContext>().SaveChangesAsync();
			await provider.GetRequiredService<ContactService>().ReconcileAsync(operation.Id, default);
		});

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.Contacts.AnyAsync(contact => contact.Id == contactId));
			Assert.Equal(ContactOperationState.Conflict, (await context.ContactOperations
				.SingleAsync(operation => operation.Kind == ContactOperationKind.Delete)).State);
		});
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ResolveConflictAsync(contactId, keepMine: false, default));
		await harness.UsingAsync(async provider =>
			Assert.Empty(await provider.GetRequiredService<MyloMailDbContext>().Contacts.ToListAsync()));
	}

	[Fact]
	public async Task Missing_provider_configuration_never_marks_contact_intent_dispatched()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Provider.FactoryFailure = new ProviderNotConfiguredException(
			ProviderType.Gmail,
			"Providers:Gmail:ClientId/ClientSecret"
		);

		await ExecutePendingAsync(harness);

		Assert.Equal(0, harness.Provider.CreateCalls);
		await harness.UsingAsync(async provider =>
			Assert.Equal(ContactOperationState.Pending, (await provider
				.GetRequiredService<MyloMailDbContext>().ContactOperations.SingleAsync()).State));
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Refresh_adoption_dispatches_a_later_user_intent()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteOnlyOperationAsync(harness));
		await harness.RestartAsync();
		await SaveAsync(harness, contactId, "Ada later", "ada@example.test");
		var laterId = await OperationIdAsync(harness, ContactOperationState.Pending);
		harness.Jobs.Created.Clear();

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		Assert.Contains(harness.Jobs.Created, job =>
			job.Method.Name == nameof(MyloMail.Api.Scheduling.ContactJobs.ExecuteAsync)
			&& Assert.IsType<Guid>(job.Args[0]) == laterId);
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Competing_ambiguous_creates_do_not_adopt_the_same_remote_contact()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var firstId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		var secondId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		harness.Provider.Seed("Ada", "ada@example.test");
		await harness.RestartAsync();
		var operationId = await OperationIdAsync(harness, ContactOperationState.Dispatched);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(operationId, default));
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			var contacts = await provider.GetRequiredService<MyloMailDbContext>().Contacts
				.OrderBy(contact => contact.Id)
				.ToListAsync();
			Assert.Contains(contacts, contact => contact.Id == firstId);
			Assert.Contains(contacts, contact => contact.Id == secondId);
			Assert.Equal(2, contacts.Count);
			Assert.All(contacts, contact => Assert.Null(contact.ProviderContactId));
		});
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Ambiguous_create_does_not_adopt_a_remote_contact_owned_by_another_local_contact()
	{
		await using var harness = await ContactHarness.CreateAsync();
		harness.Provider.Seed("Ada", "ada@example.test");
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));
		var ownedId = await harness.UsingAsync(async provider => (await provider
			.GetRequiredService<MyloMailDbContext>().Contacts.SingleAsync()).Id);
		var ambiguousId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		var operationId = await OperationIdAsync(harness, ContactOperationState.Dispatched);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(operationId, default));
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.RefreshAsync(harness.AccountId, default));

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var contacts = await context.Contacts.ToListAsync();
			Assert.NotNull(contacts.Single(contact => contact.Id == ownedId).ProviderContactId);
			Assert.Null(contacts.Single(contact => contact.Id == ambiguousId).ProviderContactId);
			Assert.NotEqual(
				ContactOperationState.Completed,
				(await context.ContactOperations.SingleAsync(
					operation => operation.ContactId == ambiguousId
				)).State
			);
		});
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Provider_timeout_during_reconciliation_schedules_another_observation()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		var operationId = await OperationIdAsync(harness, ContactOperationState.Dispatched);
		harness.Provider.PullFailure = new TaskCanceledException("Provider timeout.");
		harness.Jobs.Created.Clear();

		await Assert.ThrowsAsync<TaskCanceledException>(() => harness.UsingAsync(provider =>
			provider.GetRequiredService<ContactService>().ReconcileAsync(operationId, default)));

		Assert.Contains(harness.Jobs.Created, job =>
			job.Method.Name == nameof(MyloMail.Api.Scheduling.ContactJobs.ReconcileAsync));
		Assert.Contains(harness.AccountId, harness.Events.ContactAccounts);
	}


	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Unresolved_conflict_blocks_a_later_enqueued_edit()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		await SaveAsync(harness, contactId, "First edit", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		await SaveAsync(harness, contactId, "Later edit", "ada@example.test");
		var remote = Assert.Single(harness.Provider.Contacts);
		harness.Provider.Revise(remote.ProviderContactId, "Remote edit", "ada@example.test");
		var firstId = await OperationIdAsync(harness, ContactOperationState.Dispatched);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(firstId, default));
		var laterId = await OperationIdAsync(harness, ContactOperationState.Pending);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ExecuteAsync(laterId, default));

		Assert.Equal(0, harness.Provider.UpdateCalls);
		await harness.UsingAsync(async provider =>
		{
			var operations = await provider.GetRequiredService<MyloMailDbContext>()
				.ContactOperations.OrderBy(operation => operation.Sequence).ToListAsync();
			Assert.Equal(ContactOperationState.Conflict, operations[^2].State);
			Assert.Equal(ContactOperationState.Pending, operations[^1].State);
		});
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Not_applied_delete_retries_before_a_later_edit()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		await DeleteAsync(harness, contactId);
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		await SaveAsync(harness, contactId, "Ada restored", "ada@example.test");
		var deleteId = await OperationIdAsync(harness, ContactOperationState.Dispatched);
		harness.Jobs.Created.Clear();

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(deleteId, default));

		Assert.Contains(harness.Jobs.Created, job =>
			job.Method.Name == nameof(MyloMail.Api.Scheduling.ContactJobs.ExecuteAsync)
			&& Assert.IsType<Guid>(job.Args[0]) == deleteId);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ExecuteAsync(deleteId, default));
		await ExecutePendingAsync(harness);
		Assert.Equal("Ada restored", Assert.Single(harness.Provider.Contacts).DisplayName);
		Assert.Equal(1, harness.Provider.DeleteCalls);
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Applied_ambiguous_update_rebases_the_later_edit()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		await SaveAsync(harness, contactId, "Ada Byron", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		await SaveAsync(harness, contactId, "Ada Lovelace", "ada@example.test");
		var ambiguousId = await OperationIdAsync(harness, ContactOperationState.Dispatched);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(ambiguousId, default));
		await ExecutePendingAsync(harness);

		Assert.Equal("Ada Lovelace", Assert.Single(harness.Provider.Contacts).DisplayName);
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Keep_theirs_rebases_and_resumes_later_intent_after_a_conflict()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		await SaveAsync(harness, contactId, "First edit", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		await SaveAsync(harness, contactId, "Later intent", "ada@example.test");
		var remote = Assert.Single(harness.Provider.Contacts);
		harness.Provider.Revise(remote.ProviderContactId, "Remote edit", "ada@example.test");
		var ambiguousId = await OperationIdAsync(harness, ContactOperationState.Dispatched);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(ambiguousId, default));

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ResolveConflictAsync(contactId, keepMine: false, default));
		await ExecutePendingAsync(harness);

		Assert.Equal("Later intent", Assert.Single(harness.Provider.Contacts).DisplayName);
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Edit_behind_an_applied_ambiguous_delete_becomes_a_create()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		await ExecuteOnlyOperationAsync(harness);
		await DeleteAsync(harness, contactId);
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecutePendingAsync(harness));
		await harness.RestartAsync();
		await SaveAsync(harness, contactId, "Ada Restored", "ada@example.test");
		var ambiguousId = await OperationIdAsync(harness, ContactOperationState.Dispatched);

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(ambiguousId, default));
		await ExecutePendingAsync(harness);

		Assert.Equal("Ada Restored", Assert.Single(harness.Provider.Contacts).DisplayName);
		await harness.UsingAsync(async provider =>
			Assert.Equal(contactId, (await provider.GetRequiredService<MyloMailDbContext>()
				.Contacts.SingleAsync()).Id));
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task User_can_discard_a_create_that_remains_ambiguous()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteOnlyOperationAsync(harness));
		await harness.RestartAsync();
		var operationId = await OperationIdAsync(harness, ContactOperationState.Dispatched);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(operationId, default));

		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.AbandonAmbiguousCreateAsync(contactId, default));

		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.Contacts.ToListAsync());
			Assert.Empty(await context.ContactOperations.ToListAsync());
		});
	}



	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task A_create_response_crash_reconciles_without_creating_a_duplicate()
	{
		await using var harness = await ContactHarness.CreateAsync();
		await SaveAsync(harness, null, "Ada Lovelace", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteOnlyOperationAsync(harness));
		Assert.Single(harness.Provider.Contacts);
		await harness.RestartAsync();

		var operationId = await OnlyOperationIdAsync(harness);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(operationId, default));

		Assert.Equal(1, harness.Provider.CreateCalls);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var contact = await context.Contacts.SingleAsync();
			Assert.NotNull(contact.ProviderContactId);
			Assert.Equal(ContactOperationState.Completed, (await context.ContactOperations.SingleAsync()).State);
		});
	}

	[Trait("Category", "FaultInjection")]
	[Trait("Category", "Deep")]
	[Fact]
	public async Task Delete_behind_an_ambiguous_create_survives_restart_and_removes_the_remote_contact()
	{
		await using var harness = await ContactHarness.CreateAsync();
		var contactId = await SaveAsync(harness, null, "Ada Lovelace", "ada@example.test");
		harness.Faults.ArmAt(FaultPoints.ContactAfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteOnlyOperationAsync(harness));
		await DeleteAsync(harness, contactId);
		await harness.RestartAsync();

		var createId = await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			return await context.ContactOperations.Where(x => x.Kind == ContactOperationKind.Create)
				.Select(x => x.Id).SingleAsync();
		});
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ReconcileAsync(createId, default));
		var deleteId = await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			return await context.ContactOperations.Where(x => x.Kind == ContactOperationKind.Delete)
				.Select(x => x.Id).SingleAsync();
		});
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ExecuteAsync(deleteId, default));

		Assert.Empty(harness.Provider.Contacts);
		await harness.UsingAsync(async provider =>
			Assert.Empty(await provider.GetRequiredService<MyloMailDbContext>().Contacts.ToListAsync()));
	}

	private static Task<Guid> SaveAsync(ContactHarness harness, Guid? id, string name, string email) =>
		harness.UsingAsync(async provider =>
		{
			var revision = id is null
				? null
				: await provider.GetRequiredService<MyloMailDbContext>().Contacts
					.Where(contact => contact.Id == id)
					.Select(contact => contact.ProviderRevision)
					.SingleAsync();
			return (await provider.GetRequiredService<ContactService>().SaveAsync(
				new ContactInput(id, harness.AccountId, name, [email], revision),
				default
			)).Id;
		});
	private static Task DeleteAsync(ContactHarness harness, Guid contactId) =>
		harness.UsingAsync(async provider =>
		{
			var revision = await provider.GetRequiredService<MyloMailDbContext>().Contacts
				.Where(contact => contact.Id == contactId)
				.Select(contact => contact.ProviderRevision)
				.SingleAsync();
			await provider.GetRequiredService<ContactService>()
				.DeleteAsync(contactId, revision, default);
		});

	private static async Task ExecutePendingAsync(ContactHarness harness)
	{
		var operationId = await OperationIdAsync(harness, ContactOperationState.Pending);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ExecuteAsync(operationId, default));
	}

	private static Task<Guid> OperationIdAsync(
		ContactHarness harness,
		ContactOperationState state
	) => harness.UsingAsync(async provider =>
		await provider.GetRequiredService<MyloMailDbContext>().ContactOperations
			.Where(operation => operation.State == state)
			.OrderBy(operation => operation.Sequence)
			.Select(operation => operation.Id)
			.FirstAsync());

	private static async Task ExecuteOnlyOperationAsync(ContactHarness harness)
	{
		var operationId = await OnlyOperationIdAsync(harness);
		await harness.UsingAsync(provider => provider.GetRequiredService<ContactService>()
			.ExecuteAsync(operationId, default));
	}

	private static Task<Guid> OnlyOperationIdAsync(ContactHarness harness) => harness.UsingAsync(async provider =>
	{
		var context = provider.GetRequiredService<MyloMailDbContext>();
		return await context.ContactOperations.Where(x => x.State != ContactOperationState.Completed)
			.Select(x => x.Id).SingleAsync();
	});
}
