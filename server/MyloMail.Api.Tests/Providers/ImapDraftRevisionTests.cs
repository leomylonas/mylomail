using MailKit;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// An IMAP UID repeats after a UIDVALIDITY reset. Draft conflict preconditions must bind both
/// values or a stale local edit can overwrite a different draft that inherited the old UID.
/// </summary>
public sealed class ImapDraftRevisionTests
{
	[Fact]
	public void A_revision_round_trips_its_folder_incarnation_and_uid()
	{
		var revision = ImapMailProvider.DraftRevision(47, new UniqueId(83));

		Assert.True(ImapMailProvider.TryParseDraftRevision(revision, out var uidValidity, out var uid));
		Assert.Equal((uint)47, uidValidity);
		Assert.Equal((uint)83, uid.Id);
	}

	[Theory]
	[InlineData("83")]
	[InlineData("47:")]
	[InlineData("not-a-revision")]
	public void A_revision_without_both_uidvalidity_and_uid_is_rejected(string revision)
	{
		Assert.False(ImapMailProvider.TryParseDraftRevision(revision, out _, out _));
	}
}
