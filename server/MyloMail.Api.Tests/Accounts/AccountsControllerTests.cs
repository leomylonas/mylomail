using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyloMail.Api.Accounts;
using MyloMail.Api.Content;
using MyloMail.Api.Contracts;
using MyloMail.Api.Controllers;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Accounts;

public sealed class AccountsControllerTests
{
	/// <summary>
	/// Gmail and Graph authenticate interactively. Accepting a password for them and quietly
	/// discarding it would leave the user believing they had configured something.
	/// </summary>
	[Theory]
	[InlineData(ProviderType.Gmail)]
	[InlineData(ProviderType.Microsoft365)]
	public async Task A_password_supplied_for_an_oauth_provider_is_rejected(ProviderType type)
	{
		await using var harness = await MutationHarness.CreateAsync();

		var result = await harness.UsingAsync(services =>
			Controller(services).Add(
				new AddAccountRequest("Test", type, "someone@example.org", "hunter2", null),
				default
			)
		);

		AssertProblem(result, StatusCodes.Status400BadRequest);
	}

	/// <summary>An IMAP account cannot be reached without host, port and user name.</summary>
	[Fact]
	public async Task An_imap_account_without_settings_is_rejected()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var result = await harness.UsingAsync(services =>
			Controller(services).Add(
				new AddAccountRequest("Test", ProviderType.Imap, "someone@example.org", "hunter2", null),
				default
			)
		);

		AssertProblem(result, StatusCodes.Status400BadRequest);
	}

	/// <summary>
	/// A rejected credential is the user's to fix; a missing client registration is not, and
	/// the two must not present as the same failure.
	/// </summary>
	[Fact]
	public async Task A_rejected_credential_reports_a_bad_request()
	{
		await using var harness = await MutationHarness.CreateAsync();
		harness.Provider.FailAuthentication("wrong password");

		var result = await harness.UsingAsync(services =>
			Controller(services).Add(
				new AddAccountRequest("Test", ProviderType.Gmail, "someone@example.org", null, null),
				default
			)
		);

		AssertProblem(result, StatusCodes.Status400BadRequest);
	}

	/// <summary>A bounded initial sync with no bound is meaningless — nothing to bound by.</summary>
	[Fact]
	public async Task A_bounded_initial_sync_with_no_bound_value_is_rejected()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var result = await harness.UsingAsync(services =>
			Controller(services).Add(
				new AddAccountRequest(
					"Test",
					ProviderType.Gmail,
					"someone@example.org",
					null,
					null,
					InitialSyncMode: InitialSyncMode.LastNMonths
				),
				default
			)
		);

		AssertProblem(result, StatusCodes.Status400BadRequest);
	}

	/// <summary>The listed address comes from the default identity, not from the account row.</summary>
	[Fact]
	public async Task Listing_reports_the_address_from_the_default_identity()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Added", ProviderType.Gmail, "someone@example.org", null, null))
		);

		var listed = await harness.UsingAsync(async services =>
		{
			var response = await Controller(services).List(default);
			return Assert.IsType<OkObjectResult>(response.Result).Value as IReadOnlyList<AccountDto>;
		});

		Assert.NotNull(listed);
		Assert.Contains(listed!, a => a.EmailAddress == "someone@example.org");
	}

	/// <summary>
	/// The listed account carries the settings the form needs to open pre-filled with what
	/// was actually saved, not defaults (§1) — AccountDto previously omitted all of these.
	/// </summary>
	[Fact]
	public async Task Listing_reports_settings_saved_through_UpdateAccount()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Added", ProviderType.Gmail, "someone@example.org", null, null))
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var row = await context.Accounts.SingleAsync(a => a.Id == account.Id);
			row.PollIntervalSeconds = 120;
			row.AttachmentSizeLimitOverride = 10 * 1024 * 1024;
			await context.SaveChangesAsync();
			return true;
		});

		var listed = await harness.UsingAsync(async services =>
		{
			var response = await Controller(services).List(default);
			return Assert.IsType<OkObjectResult>(response.Result).Value as IReadOnlyList<AccountDto>;
		});

		var dto = Assert.Single(listed!, a => a.Id == account.Id);
		Assert.Equal(120, dto.PollIntervalSeconds);
		Assert.Equal(10 * 1024 * 1024, dto.AttachmentSizeLimitOverride);
	}

	/// <summary>
	/// Eighty-seventh architecture-review pass: a locked OS keychain during account creation
	/// throws <see cref="CredentialStoreUnavailableException"/> straight out of
	/// <see cref="AccountProvisioningService.AddAsync"/>, with nothing here to catch it — unlike
	/// every background job (§3), which pass 64 gave a distinct, friendly status for exactly
	/// this failure.
	/// </summary>
	[Fact]
	public async Task A_locked_credential_store_reports_service_unavailable_not_a_bare_500()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var result = await harness.UsingAsync(services =>
			ControllerWithThrowingCredentialStore(services).Add(
				new AddAccountRequest("Test", ProviderType.Imap, "someone@example.org", "hunter2", ImapSettings),
				default
			)
		);

		AssertProblem(result, StatusCodes.Status503ServiceUnavailable);
	}

	/// <summary>
	/// The symmetric case for <see cref="Reauthenticate"/>: its own credential retrieve/store
	/// calls are just as reachable from a locked keychain as <see cref="Add"/>'s, and the two
	/// catch blocks are independent edits — a mistake unique to this one (wrong status, wrong
	/// title, wrong placement relative to its own other catches) would not be caught by the
	/// <see cref="Add"/> test above.
	/// </summary>
	[Fact]
	public async Task Reauthenticating_with_a_locked_credential_store_reports_service_unavailable()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Imap, "someone@example.org", null, new CredentialPayload("imap-password", "hunter2"u8.ToArray())))
		);

		var result = await harness.UsingAsync(services =>
			ControllerWithThrowingCredentialStore(services).Reauthenticate(
				account.Id,
				new ReauthenticateAccountRequest("new-password"),
				default
			)
		);

		var problem = Assert.IsType<ObjectResult>(result);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
	}

	/// <summary>
	/// Pass 200: <see cref="AccountDto.IsThrottled"/> is read live from <see cref="AccountGate"/>
	/// at DTO-construction time, not a persisted column, so it reflects the gate's current state
	/// exactly — set the instant <see cref="AccountGate.Throttle"/> is called, and cleared the
	/// instant the window it named has elapsed, with no separate "clear" write needed anywhere.
	/// </summary>
	[Fact]
	public async Task An_account_reports_throttled_only_while_the_gate_holds_it()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var accountId = await harness.UsingAsync(async services =>
		{
			var account = await services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Throttled", ProviderType.Gmail, "someone@example.org", null, null));
			return account.Id;
		});

		var beforeThrottle = await harness.UsingAsync(async services =>
		{
			var response = await Controller(services).List(default);
			return Assert.IsType<OkObjectResult>(response.Result).Value as IReadOnlyList<AccountDto>;
		});
		Assert.False(beforeThrottle!.Single(a => a.Id == accountId).IsThrottled);

		await harness.UsingAsync(services =>
		{
			services.GetRequiredService<AccountGate>().Throttle(accountId, TimeSpan.FromMinutes(5));
			return Task.CompletedTask;
		});

		var whileThrottled = await harness.UsingAsync(async services =>
		{
			var response = await Controller(services).List(default);
			return Assert.IsType<OkObjectResult>(response.Result).Value as IReadOnlyList<AccountDto>;
		});
		Assert.True(whileThrottled!.Single(a => a.Id == accountId).IsThrottled);

		await harness.UsingAsync(services =>
		{
			services.GetRequiredService<AccountGate>().Clear(accountId);
			return Task.CompletedTask;
		});

		var afterClear = await harness.UsingAsync(async services =>
		{
			var response = await Controller(services).List(default);
			return Assert.IsType<OkObjectResult>(response.Result).Value as IReadOnlyList<AccountDto>;
		});
		Assert.False(afterClear!.Single(a => a.Id == accountId).IsThrottled);
	}

	private static readonly ImapAccountSettings ImapSettings = new(
		"imap.example.org",
		993,
		true,
		"someone@example.org",
		"smtp.example.org",
		587
	);

	private static AccountsController Controller(IServiceProvider services) =>
		new(
			services.GetRequiredService<MyloMailDbContext>(),
			services.GetRequiredService<AccountProvisioningService>(),
			services.GetRequiredService<AccountGate>()
		);

	/// <summary>
	/// A hand-built <see cref="AccountProvisioningService"/> sharing every other real dependency
	/// from the scope, but with a credential store that always throws — the only way to
	/// reproduce a locked-keychain failure without a fault-injection hook on
	/// <see cref="InMemoryCredentialStore"/> itself.
	/// </summary>
	private static AccountsController ControllerWithThrowingCredentialStore(IServiceProvider services)
	{
		var provisioning = new AccountProvisioningService(
			services.GetRequiredService<MyloMailDbContext>(),
			new ThrowingCredentialStore(),
			services.GetRequiredService<IMailProviderFactory>(),
			services.GetRequiredService<StartupScheduler>(),
			services.GetRequiredService<IHubEvents>(),
			services.GetRequiredService<SearchIndexer>(),
			services.GetRequiredService<TimeProvider>(),
			NullLogger<AccountProvisioningService>.Instance
		);
		return new AccountsController(
			services.GetRequiredService<MyloMailDbContext>(),
			provisioning,
			services.GetRequiredService<AccountGate>()
		);
	}

	private sealed class ThrowingCredentialStore : ICredentialStore
	{
		public Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct) =>
			throw new CredentialStoreUnavailableException("the keyring is locked");

		public Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct) =>
			throw new CredentialStoreUnavailableException("the keyring is locked");

		public Task DeleteAsync(Guid accountId, CancellationToken ct) =>
			throw new CredentialStoreUnavailableException("the keyring is locked");
	}

	private static void AssertProblem(ActionResult<AccountDto> result, int expectedStatus)
	{
		var problem = Assert.IsType<ObjectResult>(result.Result);
		Assert.Equal(expectedStatus, problem.StatusCode);
	}
}
