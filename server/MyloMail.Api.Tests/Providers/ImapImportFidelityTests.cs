using System.Text;
using MimeKit;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// The IMAP import hands MailKit a parsed <see cref="MimeMessage"/>, which writes it out again. A
/// message moved to another account must arrive exactly as received (its signatures cover the bytes),
/// so this pins that parsing and writing an unmodified message reproduces it.
/// </summary>
public sealed class ImapImportFidelityTests
{
	public static TheoryData<string> Messages() =>
		[
			"simple",
			"folded-headers-and-signature",
			"multipart-with-attachment",
			"eight-bit-and-quoted-printable",
		];

	private static string Sample(string name) =>
		name switch
		{
			"simple" => "From: a@example.test\r\nTo: b@example.test\r\nSubject: Hello\r\nDate: Thu, 01 Jan 2026 00:00:00 +0000\r\nMessage-ID: <1@example.test>\r\n\r\nBody\r\n",
			"folded-headers-and-signature" =>
				"DKIM-Signature: v=1; a=rsa-sha256; c=relaxed/relaxed; d=example.test; s=sel;\r\n\th=from:to:subject:date; bh=abc=; b=AAAA\r\n\tBBBB\r\nFrom: =?UTF-8?B?w4lyaWM=?= <a@example.test>\r\nTo: b@example.test,\r\n c@example.test\r\nSubject: A  long   subject with   spacing\r\nDate: Thu, 01 Jan 2026 00:00:00 +0000\r\nMessage-ID: <2@example.test>\r\n\r\nBody\r\n",
			"multipart-with-attachment" =>
				"From: a@example.test\r\nTo: b@example.test\r\nSubject: Parts\r\nDate: Thu, 01 Jan 2026 00:00:00 +0000\r\nMessage-ID: <3@example.test>\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"XX\"\r\n\r\n--XX\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello\r\n--XX\r\nContent-Type: application/octet-stream; name=\"a.bin\"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"a.bin\"\r\n\r\nAAECAwQFBgc=\r\n--XX--\r\n",
			_ =>
				"From: a@example.test\r\nTo: b@example.test\r\nSubject: Bits\r\nDate: Thu, 01 Jan 2026 00:00:00 +0000\r\nMessage-ID: <4@example.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=iso-8859-1\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nCaf=E9 =3D ok\r\n",
		};

	[Theory]
	[MemberData(nameof(Messages))]
	public void An_unmodified_message_is_written_back_byte_for_byte(string name)
	{
		var raw = Encoding.UTF8.GetBytes(Sample(name));
		using var input = new MemoryStream(raw);
		var message = MimeMessage.Load(input);

		var options = FormatOptions.Default.Clone();
		options.NewLineFormat = NewLineFormat.Dos;
		using var output = new MemoryStream();
		message.WriteTo(options, output);

		Assert.Equal(Encoding.UTF8.GetString(raw), Encoding.UTF8.GetString(output.ToArray()));
	}
}
