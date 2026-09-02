using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Imap;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// User-approved feature, following a forty-third-pass finding: a server that doesn't
/// advertise RFC 6154 SPECIAL-USE reports every folder as <see cref="SpecialUse.None"/>,
/// silently breaking anything depending on finding Sent/Trash/Drafts. §13 Epic 2's "sensible
/// handling where a provider doesn't support an operation" now includes a modest,
/// English-centric name-based fallback — see <see cref="ImapMailProvider.SpecialUseFromName"/>.
/// </summary>
public sealed class ImapSpecialUseFallbackTests
{
	[Theory]
	[InlineData("Sent", SpecialUse.Sent)]
	[InlineData("Sent Items", SpecialUse.Sent)]
	[InlineData("sent mail", SpecialUse.Sent)]
	[InlineData("Trash", SpecialUse.Trash)]
	[InlineData("Deleted Items", SpecialUse.Trash)]
	[InlineData("Deleted Messages", SpecialUse.Trash)]
	[InlineData("Bin", SpecialUse.Trash)]
	[InlineData("Drafts", SpecialUse.Drafts)]
	[InlineData("Archive", SpecialUse.Archive)]
	[InlineData("All Mail", SpecialUse.Archive)]
	[InlineData("Junk", SpecialUse.Junk)]
	[InlineData("Junk E-Mail", SpecialUse.Junk)]
	[InlineData("Spam", SpecialUse.Junk)]
	public void A_well_known_folder_name_is_recognised(string name, SpecialUse expected) =>
		Assert.Equal(expected, ImapMailProvider.SpecialUseFromName(name));

	[Theory]
	[InlineData("Inbox")]
	[InlineData("Projects")]
	[InlineData("Receipts")]
	[InlineData("")]
	public void An_unrecognised_folder_name_falls_back_to_none(string name) =>
		Assert.Equal(SpecialUse.None, ImapMailProvider.SpecialUseFromName(name));

	[Fact]
	public void Matching_is_case_and_whitespace_insensitive() =>
		Assert.Equal(SpecialUse.Sent, ImapMailProvider.SpecialUseFromName("  sent items  "));
}
