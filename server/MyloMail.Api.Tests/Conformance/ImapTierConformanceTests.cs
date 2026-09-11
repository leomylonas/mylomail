using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>
/// The IMAP capability matrix as conformance subjects. Servers differ sharply on `MOVE`,
/// `UIDPLUS`, `CONDSTORE`, `QRESYNC`, namespaces and hierarchy delimiters, and the
/// abstraction assumes more uniformity than exists — so one test server proves almost
/// nothing (§11).
/// </summary>
/// <remarks>
/// Start them with <c>pnpm imap:up</c>. Absent, every case skips.
/// </remarks>
public abstract class ImapConformanceTests : MailProviderConformanceTests
{
	protected abstract string Tier { get; }

	private string? Host => Environment.GetEnvironmentVariable($"TEST_IMAP_{Tier}_HOST");

	private string? Port => Environment.GetEnvironmentVariable($"TEST_IMAP_{Tier}_PORT");

	protected override string? SkipReason =>
		Host is null || Port is null
			? $"TEST_IMAP_{Tier}_HOST/PORT not set — start the matrix with `pnpm imap:up`"
			: null;

	protected override Task<IConformanceHarness> CreateHarnessAsync() =>
		ImapConformanceHarness
			.CreateAsync(
				new ImapConnectionSettings(
					Host!,
					int.Parse(Port!),
					ImapSecurity: MailTransportSecurity.StartTls,
					UserName: Environment.GetEnvironmentVariable("TEST_IMAP_USER") ?? "test@mylomail.local",
					Password: Environment.GetEnvironmentVariable("TEST_IMAP_PASSWORD") ?? "password",
					CertificateTrustMode: CertificateTrustMode.TrustAll
				)
			)
			.ContinueWith(t => (IConformanceHarness)t.Result, TaskScheduler.Default);

	[SkippableFact]
	public async Task Move_to_trash_preserves_the_message_in_the_trash_mailbox()
	{
		Skip.If(SkipReason is not null, SkipReason ?? string.Empty);
		var harness = Assert.IsType<ImapConformanceHarness>(Harness);
		var occurrence = (await harness.SeedMessageAsync(harness.Source)) with
		{
			ResolvedTargetMailboxId = harness.Trash.Id,
		};

		var result = await harness.Provider.MoveToTrashAsync(
			harness.Account,
			[occurrence],
			CancellationToken.None
		);

		var item = Assert.Single(result.Items);
		Assert.True(item.Succeeded, item.Problem?.Detail);
		Assert.Contains(
			item.OccurrenceChanges,
			change => change.MailboxId == harness.Source.Id && change.Removed
		);

		var staleOriginal = (await harness.SeedMessageAsync(harness.Source)) with
		{
			ResolvedTargetMailboxId = harness.Trash.Id,
		};
		var stale = staleOriginal with { ProviderOccurrenceId = "4294967000" };
		var staleResult = await harness.Provider.MoveToTrashAsync(
			harness.Account,
			[stale],
			CancellationToken.None
		);
		var staleItem = Assert.Single(staleResult.Items);
		Assert.False(staleItem.Succeeded);
		Assert.Equal("NOTFOUND", staleItem.Problem?.ProviderCode);

		var source = await harness.Provider.InitialSyncMailboxAsync(
			harness.Account,
			harness.Source,
			null,
			InitialSyncMode.Full,
			null,
			int.MaxValue,
			CancellationToken.None
		);
		var trash = await harness.Provider.InitialSyncMailboxAsync(
			harness.Account,
			harness.Trash,
			null,
			InitialSyncMode.Full,
			null,
			int.MaxValue,
			CancellationToken.None
		);
		var messageIdHeader = $"<{occurrence.MessageId:N}@mylomail.local>";
		var staleMessageIdHeader = $"<{stale.MessageId:N}@mylomail.local>";
		Assert.DoesNotContain(source.Messages, message => message.MessageIdHeader == messageIdHeader);
		Assert.Contains(
			source.Messages,
			message => message.MessageIdHeader == staleMessageIdHeader
		);
		Assert.Contains(trash.Messages, message => message.MessageIdHeader == messageIdHeader);
		Assert.DoesNotContain(
			trash.Messages,
			message => message.MessageIdHeader == staleMessageIdHeader
		);
	}
}

/// <summary>CONDSTORE, QRESYNC, UIDPLUS and MOVE.</summary>
public sealed class ImapQResyncConformanceTests : ImapConformanceTests
{
	protected override string Tier => "QRESYNC";
}

/// <summary>CONDSTORE without QRESYNC, and a `.` delimiter behind an `INBOX.` prefix.</summary>
public sealed class ImapCondStoreConformanceTests : ImapConformanceTests
{
	protected override string Tier => "CONDSTORE";
}

/// <summary>Neither, and no UIDPLUS or MOVE — a move cannot report its destination UID.</summary>
public sealed class ImapBasicConformanceTests : ImapConformanceTests
{
	protected override string Tier => "BASIC";
}
