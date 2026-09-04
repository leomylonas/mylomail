using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// Hundred-and-tenth architecture-review pass: <see cref="ImapProviderConfig.AppendToSentOnSend"/>
/// was a real, correctly-consumed setting (<c>MailProviderFactory</c> reads it to decide whether
/// the client should file a Sent copy) with no way for a user to change it — absent from
/// <see cref="AccountDto"/> and <see cref="AccountSettingsDto"/>, no UI control anywhere.
/// </summary>
public sealed class AppendToSentSettingsTests
{
	[Fact]
	public async Task Toggling_it_for_an_imap_account_updates_only_that_field_on_the_provider_config()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			account.ProviderConfig = new ImapProviderConfig
			{
				Host = "imap.example.test",
				Port = 993,
				UserName = "someone",
				SmtpHost = "smtp.example.test",
				SmtpPort = 587,
				AppendToSentOnSend = true,
			};
			await context.SaveChangesAsync();
		});

		var settings = AccountSettings(harness.Account, appendToSentOnSend: false);
		var applied = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);

		Assert.False(applied.AppendToSentOnSend);

		var reloaded = await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			return (ImapProviderConfig)(await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id)).ProviderConfig!;
		});
		Assert.False(reloaded.AppendToSentOnSend);
		// Untouched: only AppendToSentOnSend should change on this write.
		Assert.Equal("imap.example.test", reloaded.Host);
		Assert.Equal(993, reloaded.Port);
		Assert.Equal("smtp.example.test", reloaded.SmtpHost);
	}

	[Fact]
	public async Task A_non_imap_account_ignores_the_field_instead_of_throwing()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var settings = AccountSettings(harness.Account, appendToSentOnSend: false);

		var applied = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);

		Assert.Null(applied.AppendToSentOnSend);
	}

	private static AccountSettingsDto AccountSettings(Account account, bool appendToSentOnSend) =>
		new(
			account.Id,
			account.DisplayName,
			account.Color,
			account.PollIntervalSeconds,
			account.PollingEnabled,
			account.UndoSendDelaySeconds,
			account.NotificationsEnabled,
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			appendToSentOnSend
		);
}
