using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyloMail.Api.Accounts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Accounts;

public sealed class AccountProvisioningTests
{
	[Fact]
	public async Task Adding_an_account_creates_its_authoritative_send_identity()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var account = await AddAsync(harness, "someone@example.org");

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var identity = await context.SendIdentities.SingleAsync(i => i.AccountId == account.Id);

			// Account deliberately has no address column; the default identity carries it.
			Assert.True(identity.IsDefault);
			Assert.Equal("someone@example.org", identity.EmailAddress);
		});
	}

	/// <summary>Credentials go to the store, never to a column on the account (§4).</summary>
	[Fact]
	public async Task The_secret_reaches_the_credential_store_and_not_the_account_row()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var account = await AddAsync(harness, "someone@example.org", Secret());

		await harness.UsingAsync(async services =>
		{
			var stored = await services
				.GetRequiredService<ICredentialStore>()
				.RetrieveAsync(account.Id, CancellationToken.None);

			Assert.NotNull(stored);
			Assert.Equal("hunter2"u8.ToArray(), stored!.Data);
		});
	}

	/// <summary>
	/// A rejected account leaves nothing behind — including the credential, which was
	/// necessarily stored before authentication could be attempted.
	/// </summary>
	[Fact]
	public async Task A_rejected_account_leaves_no_orphaned_credential()
	{
		await using var harness = await MutationHarness.CreateAsync();
		harness.Provider.FailAuthentication("wrong password");

		// A spy, because the orphan's id is generated inside the service: asserting the store
		// looks empty afterwards would pass even if nothing were ever written.
		var spy = new RecordingCredentialStore();

		await Assert.ThrowsAnyAsync<Exception>(() =>
			harness.UsingAsync(services =>
				new AccountProvisioningService(
					services.GetRequiredService<MyloMailDbContext>(),
					spy,
					services.GetRequiredService<MyloMail.Api.Providers.IMailProviderFactory>(),
					services.GetRequiredService<MyloMail.Api.Scheduling.StartupScheduler>(),
					services.GetRequiredService<MyloMail.Api.Hubs.IHubEvents>(),
					services.GetRequiredService<MyloMail.Api.Content.SearchIndexer>(),
					TimeProvider.System,
					services.GetRequiredService<ILogger<AccountProvisioningService>>()
				).AddAsync(new NewAccount("Test", ProviderType.Gmail, "someone@example.org", null, Secret()))
			)
		);

		var stored = Assert.Single(spy.Stored);
		Assert.Contains(stored, spy.Deleted);

		await harness.UsingAsync(async services =>
			// Only the harness's own account; the rejected one was never committed.
			Assert.Single(await services.GetRequiredService<MyloMailDbContext>().Accounts.ToListAsync())
		);
	}

	/// <summary>
	/// A certificate-untrusted rejection's fingerprint/hostname reach the controller, not just
	/// its message text — <c>AddAccount</c>'s "Trust this certificate" prompt needs the
	/// structured data, not prose to parse (§15).
	/// </summary>
	[Fact]
	public async Task A_certificate_rejection_carries_its_fingerprint_and_hostname_through()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var problem = MyloMail.Api.Security.CertificateTrust.Problem(
			"mail.example.test",
			"deadbeef",
			"CN=Self-Signed"
		);
		harness.Provider.FailAuthentication(problem);

		var failure = await Assert.ThrowsAsync<AccountAuthenticationFailedException>(() =>
			AddAsync(harness, "someone@example.org", Secret())
		);

		Assert.NotNull(failure.Problem);
		Assert.Equal(ErrorCategory.Validation, failure.Problem!.Category);
		Assert.Equal("mail.example.test", failure.Problem.Extensions["hostname"]);
		Assert.Equal("deadbeef", failure.Problem.Extensions["sha256Fingerprint"]);
	}

	[Fact]
	public async Task Reauthenticating_with_a_new_secret_succeeds_and_resumes_the_account()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Imap, "someone@example.org", null, Secret()))
		);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var stuck = await context.Accounts.SingleAsync(a => a.Id == account.Id);
			stuck.AuthState = AuthState.NeedsReauth;
			stuck.LastAuthError = "wrong password";
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().ReauthenticateAsync(account.Id, "new-password")
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var resumed = await context.Accounts.SingleAsync(a => a.Id == account.Id);
			Assert.Equal(AuthState.Connected, resumed.AuthState);
			Assert.Null(resumed.LastAuthError);

			var stored = await services.GetRequiredService<ICredentialStore>().RetrieveAsync(account.Id, CancellationToken.None);
			Assert.Equal("new-password"u8.ToArray(), stored!.Data);
		});
	}

	[Fact]
	public async Task Reauthenticating_an_OAuth_IMAP_account_preserves_the_token_format()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(
					new NewAccount(
						"Test",
						ProviderType.Imap,
						"someone@example.org",
						new ImapProviderConfig { AuthMethod = ImapAuthMethod.OAuth2 },
						new CredentialPayload(
							MailProviderFactory.ImapOAuth2TokenFormat,
							"old-token"u8.ToArray()
						)
					)
				)
		);

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().ReauthenticateAsync(account.Id, "new-token")
		);

		await harness.UsingAsync(async services =>
		{
			var stored = await services
				.GetRequiredService<ICredentialStore>()
				.RetrieveAsync(account.Id, CancellationToken.None);
			Assert.Equal(MailProviderFactory.ImapOAuth2TokenFormat, stored!.Format);
			Assert.Equal("new-token"u8.ToArray(), stored.Data);
		});
	}

	/// <summary>
	/// A certificate-only rejection (rotated server certificate, now pinned) needs no new
	/// secret at all — the stored credential was never wrong.
	/// </summary>
	[Fact]
	public async Task Reauthenticating_with_no_secret_re_verifies_the_existing_credential()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Imap, "someone@example.org", null, Secret()))
		);

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().ReauthenticateAsync(account.Id, secret: null)
		);

		await harness.UsingAsync(async services =>
		{
			var stored = await services.GetRequiredService<ICredentialStore>().RetrieveAsync(account.Id, CancellationToken.None);
			// Untouched — reauthenticating never re-stored a secret it was never given.
			Assert.Equal("hunter2"u8.ToArray(), stored!.Data);
		});
	}

	[Fact]
	public async Task A_still_rejected_reauthentication_records_the_new_error_and_stays_paused()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Imap, "someone@example.org", null, Secret()))
		);
		harness.Provider.FailAuthentication("still wrong");

		await Assert.ThrowsAsync<AccountAuthenticationFailedException>(() =>
			harness.UsingAsync(services =>
				services.GetRequiredService<AccountProvisioningService>().ReauthenticateAsync(account.Id, "guess-again")
			)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var stillStuck = await context.Accounts.SingleAsync(a => a.Id == account.Id);
			Assert.Equal("still wrong", stillStuck.LastAuthError);
		});
	}

	/// <summary>
	/// A rejection unrelated to the new password (a transient failure, or an unpinned
	/// certificate) must not cost the account its last known-working credential — only the
	/// still-untested guess should be gone, not the password that used to work.
	/// </summary>
	[Fact]
	public async Task A_failed_reauthentication_restores_the_previously_working_credential()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Imap, "someone@example.org", null, Secret()))
		);
		harness.Provider.FailAuthentication("still wrong");

		await Assert.ThrowsAsync<AccountAuthenticationFailedException>(() =>
			harness.UsingAsync(services =>
				services.GetRequiredService<AccountProvisioningService>().ReauthenticateAsync(account.Id, "a-bad-guess")
			)
		);

		await harness.UsingAsync(async services =>
		{
			var stored = await services.GetRequiredService<ICredentialStore>().RetrieveAsync(account.Id, CancellationToken.None);
			// The original "hunter2" from Secret(), not the failed "a-bad-guess" attempt.
			Assert.Equal("hunter2"u8.ToArray(), stored!.Data);
		});
	}

	private sealed class RecordingCredentialStore : ICredentialStore
	{
		public List<Guid> Stored { get; } = [];

		public List<Guid> Deleted { get; } = [];

		public Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct)
		{
			Stored.Add(accountId);
			return Task.CompletedTask;
		}

		public Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct) =>
			Task.FromResult<CredentialPayload?>(null);

		public Task DeleteAsync(Guid accountId, CancellationToken ct)
		{
			Deleted.Add(accountId);
			return Task.CompletedTask;
		}
	}

	[Fact]
	public async Task Removing_an_account_with_a_nested_mailbox_hierarchy_does_not_throw()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await AddAsync(harness, "someone@example.org", Secret());

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var parent = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "PARENT",
				Name = "Parent",
				SpecialUse = SpecialUse.None,
			};
			context.Mailboxes.Add(parent);
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					ProviderMailboxId = "CHILD",
					Name = "Child",
					SpecialUse = SpecialUse.None,
					ParentId = parent.Id,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().RemoveAsync(account.Id)
		);
	}

	/// <summary>Removal disables first, so a running job stops before the row disappears (§3).</summary>
	[Fact]
	public async Task Removing_an_account_deletes_it_and_its_credential()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await AddAsync(harness, "someone@example.org", Secret());

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().RemoveAsync(account.Id)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Null(await context.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id));
			Assert.Null(
				await services.GetRequiredService<ICredentialStore>().RetrieveAsync(account.Id, CancellationToken.None)
			);
		});
	}

	/// <summary>
	/// A running export writes local files for the account being removed — the user should get
	/// a say (per an explicit product decision) rather than losing it silently. An unforced
	/// removal must refuse, leaving both the account and the export untouched.
	/// </summary>
	[Fact]
	public async Task Removing_an_account_with_a_running_export_refuses_unless_forced()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await AddAsync(harness, "someone@example.org", Secret());
		var exportId = Guid.NewGuid();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.ExportJobs.Add(
				new ExportJob
				{
					Id = exportId,
					AccountId = account.Id,
					DestinationPath = "/tmp/export",
					Status = ExportJobStatus.Running,
					TotalCount = 10,
					WrittenCount = 3,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async services =>
		{
			var ex = await Assert.ThrowsAsync<ExportInProgressException>(
				() => services.GetRequiredService<AccountProvisioningService>().RemoveAsync(account.Id)
			);
			Assert.Equal(exportId, ex.ExportId);
		});

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.NotNull(await context.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id));
			Assert.Equal(ExportJobStatus.Running, (await context.ExportJobs.SingleAsync(j => j.Id == exportId)).Status);
		});
	}

	/// <summary>
	/// Forcing removal despite a running export lets removal proceed instead of throwing
	/// <see cref="ExportInProgressException"/>. This does not independently observe the
	/// explicit cancel-before-delete step in <c>RemoveAsync</c> — cascade delete removes the
	/// <see cref="ExportJob"/> row along with the account either way, so a test asserting the
	/// row is gone can't tell that step apart from simply not throwing.
	/// </summary>
	[Fact]
	public async Task Removing_an_account_with_a_running_export_when_forced_stops_the_export_and_removes_it()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await AddAsync(harness, "someone@example.org", Secret());
		var exportId = Guid.NewGuid();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.ExportJobs.Add(
				new ExportJob
				{
					Id = exportId,
					AccountId = account.Id,
					DestinationPath = "/tmp/export",
					Status = ExportJobStatus.Running,
					TotalCount = 10,
					WrittenCount = 3,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().RemoveAsync(account.Id, force: true)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Null(await context.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id));
			// Cascade delete removes the row along with the account — what matters here is that
			// force actually let removal proceed rather than throwing.
			Assert.Null(await context.ExportJobs.FirstOrDefaultAsync(j => j.Id == exportId));
		});
	}

	[Fact]
	public async Task A_bounded_initial_sync_choice_is_persisted_on_the_account()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(
					new NewAccount(
						"Test",
						ProviderType.Gmail,
						"someone@example.org",
						null,
						null,
						InitialSyncMode: InitialSyncMode.LastNMonths,
						InitialSyncBoundValue: 3
					)
				)
		);

		Assert.Equal(InitialSyncMode.LastNMonths, account.InitialSyncMode);
		Assert.Equal(3, account.InitialSyncBoundValue);
	}

	[Fact]
	public async Task An_unbounded_initial_sync_choice_carries_no_bound_value_even_if_one_was_supplied()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(
					new NewAccount(
						"Test",
						ProviderType.Gmail,
						"someone@example.org",
						null,
						null,
						// A stray bound value with Full mode is meaningless and must not be
						// stored as if it meant something.
						InitialSyncMode: InitialSyncMode.Full,
						InitialSyncBoundValue: 3
					)
				)
		);

		Assert.Equal(InitialSyncMode.Full, account.InitialSyncMode);
		Assert.Null(account.InitialSyncBoundValue);
	}

	private static CredentialPayload Secret() => new("imap-password", "hunter2"u8.ToArray());

	private static Task<Account> AddAsync(MutationHarness harness, string address, CredentialPayload? secret = null) =>
		harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Gmail, address, null, secret))
		);
}
