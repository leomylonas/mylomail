using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Tests.Fakes;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>
/// Runs the shared conformance suite against <see cref="FakeMailProvider"/> wearing the
/// shape of each provider and each IMAP capability tier.
/// </summary>
/// <remarks>
/// This exists so the suite is known to discriminate before a real provider is written. A
/// conformance suite first exercised by the code it is meant to judge proves nothing: it
/// will have been shaped, unconsciously, to pass. These cases are cheap and run everywhere.
/// </remarks>
public sealed class FakeConformanceHarness : IConformanceHarness
{
	private const string SourceProviderId = "INBOX";
	private const string DestinationProviderId = "Archive";

	private readonly FakeMailProvider provider;

	public FakeConformanceHarness(ProviderCapabilities capabilities)
	{
		provider = new FakeMailProvider(capabilities);
		provider.AddMailbox(SourceProviderId, SpecialUse.Inbox);
		provider.AddMailbox(DestinationProviderId, SpecialUse.Archive);

		Account = new Account
		{
			Id = Guid.NewGuid(),
			DisplayName = "Conformance",
			ProviderType = capabilities.Type,
			AuthState = AuthState.Connected,
		};

		Source = new Mailbox
		{
			Id = Guid.NewGuid(),
			AccountId = Account.Id,
			ProviderMailboxId = SourceProviderId,
			Name = SourceProviderId,
			SpecialUse = SpecialUse.Inbox,
		};

		Destination = new Mailbox
		{
			Id = Guid.NewGuid(),
			AccountId = Account.Id,
			ProviderMailboxId = DestinationProviderId,
			Name = DestinationProviderId,
			SpecialUse = SpecialUse.Archive,
		};
	}

	public IMailProvider Provider => provider;

	public Account Account { get; }

	public Mailbox Source { get; }

	public Mailbox Destination { get; }

	public Task<MessageOccurrenceRef> SeedMessageAsync(Mailbox mailbox, CancellationToken ct = default)
	{
		var messageId = Guid.NewGuid();
		var occurrenceId = provider.SeedMessage(
			mailbox.ProviderMailboxId!,
			messageId,
			DateTimeOffset.UtcNow
		);
		return Task.FromResult(new MessageOccurrenceRef(messageId, mailbox.Id, occurrenceId));
	}

	public Task SeedPageOverflowAsync(Mailbox mailbox, CancellationToken ct = default)
	{
		for (var i = 0; i < 60; i++)
		{
			provider.SeedMessage(mailbox.ProviderMailboxId!, Guid.NewGuid(), DateTimeOffset.UtcNow);
		}

		return Task.CompletedTask;
	}

	public Task<ProviderCursorState> BaselineCursorAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	) => Task.FromResult(provider.CurrentCursor());

	public Task<ProviderCursorState> ExpiredCursorAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	)
	{
		var stale = provider.CurrentCursor();
		provider.InvalidateCursors();
		return Task.FromResult(stale);
	}

	public Task<MessageOccurrenceRef> UnresolvableOccurrenceAsync(
		Mailbox mailbox,
		CancellationToken ct = default
	) => Task.FromResult(new MessageOccurrenceRef(Guid.NewGuid(), mailbox.Id, "occ-does-not-exist"));

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class GmailShapeConformanceTests : MailProviderConformanceTests
{
	protected override Task<IConformanceHarness> CreateHarnessAsync() =>
		Task.FromResult<IConformanceHarness>(
			new FakeConformanceHarness(ProviderShapes.Gmail)
		);
}

public sealed class GraphShapeConformanceTests : MailProviderConformanceTests
{
	protected override Task<IConformanceHarness> CreateHarnessAsync() =>
		Task.FromResult<IConformanceHarness>(
			new FakeConformanceHarness(ProviderShapes.Graph)
		);
}

public sealed class ImapQResyncShapeConformanceTests : MailProviderConformanceTests
{
	protected override Task<IConformanceHarness> CreateHarnessAsync() =>
		Task.FromResult<IConformanceHarness>(
			new FakeConformanceHarness(ProviderShapes.Imap(ImapCapabilityTier.QResync))
		);
}

public sealed class ImapCondStoreShapeConformanceTests : MailProviderConformanceTests
{
	protected override Task<IConformanceHarness> CreateHarnessAsync() =>
		Task.FromResult<IConformanceHarness>(
			new FakeConformanceHarness(ProviderShapes.Imap(ImapCapabilityTier.CondStore))
		);
}

public sealed class ImapBasicShapeConformanceTests : MailProviderConformanceTests
{
	protected override Task<IConformanceHarness> CreateHarnessAsync() =>
		Task.FromResult<IConformanceHarness>(
			new FakeConformanceHarness(ProviderShapes.Imap(ImapCapabilityTier.Basic))
		);
}
