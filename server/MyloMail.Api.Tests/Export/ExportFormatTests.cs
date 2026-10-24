using System.Text;
using MyloMail.Api.Export;
using Xunit;

namespace MyloMail.Api.Tests.Export;

public sealed class ExportFormatTests
{
	private static string Mbox(string sender, DateTimeOffset received, string message)
	{
		using var stream = new MemoryStream();
		MboxFormat.Append(stream, sender, received, Encoding.ASCII.GetBytes(message));
		return Encoding.ASCII.GetString(stream.ToArray());
	}

	[Fact]
	public void A_message_is_introduced_by_a_from_line_with_a_two_column_day_and_ends_with_a_blank_line()
	{
		var text = Mbox("a@example.test", new DateTimeOffset(2026, 10, 1, 9, 5, 7, TimeSpan.Zero), "Subject: x\n\nBody\n");

		Assert.Equal("From a@example.test Thu Oct  1 09:05:07 2026\nSubject: x\n\nBody\n\n", text);
	}

	[Fact]
	public void Lines_that_look_like_a_message_start_are_quoted_so_readers_can_restore_them()
	{
		var text = Mbox("a@example.test", DateTimeOffset.UnixEpoch, "Subject: x\n\nFrom here\n>From there\nnot From\n");

		Assert.Contains("\n>From here\n", text);
		Assert.Contains("\n>>From there\n", text);
		Assert.Contains("\nnot From\n", text);
	}

	[Fact]
	public void A_message_without_a_final_newline_gets_one_and_an_unusable_sender_becomes_mailer_daemon()
	{
		var text = Mbox("two words", DateTimeOffset.UnixEpoch, "Subject: x\n\nlast");

		Assert.StartsWith("From MAILER-DAEMON ", text, StringComparison.Ordinal);
		Assert.EndsWith("last\n\n", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Re: Q3 / final?", "Re_ Q3 _ final_")]
	[InlineData("", "x")]
	[InlineData("CON", "x")]
	[InlineData("..", "x")]
	[InlineData("trailing dots...", "trailing dots")]
	public void Names_are_made_safe_for_every_filesystem(string text, string expected) =>
		Assert.Equal(expected, ExportNames.Segment(text, "x"));

	[Fact]
	public void A_long_name_is_cut_and_folders_that_collapse_to_one_name_are_kept_apart_ignoring_case()
	{
		Assert.Equal(100, ExportNames.Segment(new string('a', 300), "x").Length);

		var taken = new HashSet<string>();
		Assert.Equal("Bills", ExportNames.Unique("Bills", taken));
		Assert.Equal("bills (2)", ExportNames.Unique("bills", taken));
		Assert.Equal("BILLS (3)", ExportNames.Unique("BILLS", taken));
	}
}
