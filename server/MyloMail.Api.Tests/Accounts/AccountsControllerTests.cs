using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Accounts;
using MyloMail.Api.Contracts;
using MyloMail.Api.Controllers;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
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

	private static AccountsController Controller(IServiceProvider services) =>
		new(
			services.GetRequiredService<MyloMailDbContext>(),
			services.GetRequiredService<AccountProvisioningService>()
		);

	private static void AssertProblem(ActionResult<AccountDto> result, int expectedStatus)
	{
		var problem = Assert.IsType<ObjectResult>(result.Result);
		Assert.Equal(expectedStatus, problem.StatusCode);
	}
}
