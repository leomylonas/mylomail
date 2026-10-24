using System.Globalization;
using System.Text;

namespace MyloMail.Api.Export;

/// <summary>
/// The mbox "mboxrd" variant: each message is preceded by a <c>From </c> line, and any line in the
/// message that begins with <c>From </c> (after any number of <c>&gt;</c>) gains a leading <c>&gt;</c>
/// so it cannot be taken for the start of the next message. Readers undo exactly that.
/// </summary>
internal static class MboxFormat
{
	private static readonly byte[] From = "From "u8.ToArray();

	public static void Append(Stream output, string envelopeSender, DateTimeOffset received, ReadOnlySpan<byte> message)
	{
		var sender = string.IsNullOrWhiteSpace(envelopeSender) || envelopeSender.Any(char.IsWhiteSpace) ? "MAILER-DAEMON" : envelopeSender;
		// asctime pads the day to two columns: "Oct  1", not "Oct 1".
		var utc = received.UtcDateTime;
		var date = string.Create(
			CultureInfo.InvariantCulture,
			$"{utc:ddd MMM} {utc.Day,2} {utc:HH:mm:ss yyyy}"
		);

		output.Write(Encoding.ASCII.GetBytes($"From {sender} {date}\n"));

		var atLineStart = true;
		var lastWasNewline = true;
		for (var i = 0; i < message.Length; i++)
		{
			if (atLineStart && QuotesFrom(message[i..]))
			{
				output.WriteByte((byte)'>');
			}

			output.WriteByte(message[i]);
			atLineStart = message[i] == (byte)'\n';
			lastWasNewline = atLineStart;
		}

		if (!lastWasNewline)
		{
			output.WriteByte((byte)'\n');
		}

		// The blank line that ends the message and separates it from the next.
		output.WriteByte((byte)'\n');
	}

	/// <summary>Whether the line starting here is <c>&gt;*From </c>.</summary>
	private static bool QuotesFrom(ReadOnlySpan<byte> line)
	{
		var i = 0;
		while (i < line.Length && line[i] == (byte)'>')
		{
			i++;
		}

		return line[i..].StartsWith(From);
	}
}
