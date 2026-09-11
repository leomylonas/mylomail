using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contacts;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Contacts;

public sealed class ContactSuggestionTests
{
	[Fact]
	public async Task Message_addresses_are_suggestions_but_saved_contacts_take_precedence()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(provider);
			var mailbox = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderMailboxId = "INBOX",
				Name = "Inbox",
				SpecialUse = SpecialUse.Inbox,
				IsSubscribed = true,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			var message = new MessageDto
			{
				Occurrences = [new MessageOccurrenceDto("INBOX", "1")],
				From = [new Address("Observed Ada", "ADA@example.test")],
				ReceivedAt = DateTimeOffset.UnixEpoch,
			};
			var result = await new MessageIngestor(context, new ContactSuggestionService(context)).IngestAsync(
				account,
				[message],
				new Dictionary<string, Mailbox> { ["INBOX"] = mailbox },
				GenerationSnapshot.Capture([mailbox])
			);
			Assert.True(result.ContactSuggestionsChanged);
			await context.SaveChangesAsync();
			var contacts = provider.GetRequiredService<ContactService>();
			var observed = Assert.Single(await contacts.ListSuggestionsAsync(account.Id, default));
			Assert.Equal("Observed Ada", observed.DisplayName);
			Assert.Equal("ADA@example.test", Assert.Single(observed.Emails));

			await contacts.SaveAsync(
				new ContactInput(null, account.Id, "Saved Ada", ["ada@example.test"], null),
				default
			);
			var authoritative = Assert.Single(await contacts.ListSuggestionsAsync(account.Id, default));
			Assert.Equal("Saved Ada", authoritative.DisplayName);
			Assert.Equal("ada@example.test", Assert.Single(authoritative.Emails));
			Assert.Equal(1, await context.ContactSuggestions.CountAsync());
		});
	}

	[Fact]
	public async Task Concurrent_observations_upsert_one_suggestion_without_losing_the_caller_commit()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(async firstProvider =>
		{
			var account = await harness.AccountInScopeAsync(firstProvider);
			var firstContext = firstProvider.GetRequiredService<MyloMailDbContext>();
			Assert.True(await new ContactSuggestionService(firstContext).ObserveAsync(
				account.Id,
				[new Address("First Ada", "ada@example.test")]
			));

			await harness.UsingAsync(async secondProvider =>
			{
				var secondContext = secondProvider.GetRequiredService<MyloMailDbContext>();
				Assert.True(await new ContactSuggestionService(secondContext).ObserveAsync(
					account.Id,
					[new Address("Second Ada", "ADA@example.test")]
				));
				await secondContext.SaveChangesAsync();
			});

			await firstContext.SaveChangesAsync();
		});

		await harness.UsingAsync(async provider =>
		{
			var suggestion = await provider.GetRequiredService<MyloMailDbContext>()
				.ContactSuggestions.SingleAsync();
			Assert.Equal("ada@example.test", suggestion.NormalizedEmail);
			Assert.Equal("Second Ada", suggestion.DisplayName);
		});
	}

	[Fact]
	public async Task Malformed_message_addresses_are_not_offered_as_suggestions()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(async provider =>
		{
			var account = await harness.AccountInScopeAsync(provider);
			var context = provider.GetRequiredService<MyloMailDbContext>();

			Assert.False(await new ContactSuggestionService(context).ObserveAsync(
				account.Id,
				[new Address("Invalid", "not-an-address")]
			));
			Assert.Empty(await context.ContactSuggestions.ToListAsync());
		});
	}
}
