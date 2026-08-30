using MyloMail.Api.Providers.Imap;

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
					UseSsl: false,
					Environment.GetEnvironmentVariable("TEST_IMAP_USER") ?? "test@mylomail.local",
					Environment.GetEnvironmentVariable("TEST_IMAP_PASSWORD") ?? "password"
				)
			)
			.ContinueWith(t => (IConformanceHarness)t.Result, TaskScheduler.Default);
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
