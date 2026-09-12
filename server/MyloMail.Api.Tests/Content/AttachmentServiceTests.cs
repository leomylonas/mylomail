using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Content;

public sealed class AttachmentServiceTests
{
	[Fact]
	public async Task Reads_the_stored_raw_part_and_materialises_a_private_copy()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var messageId = Guid.NewGuid();
		var attachmentId = Guid.NewGuid();
		var payload = "private attachment"u8.ToArray();

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var mime = new MimeMessage();
			mime.From.Add(MailboxAddress.Parse("sender@example.org"));
			mime.To.Add(MailboxAddress.Parse("reader@example.org"));
			var body = new BodyBuilder { TextBody = "body" };
			body.Attachments.Add("report.txt", payload, ContentType.Parse("text/plain"));
			mime.Body = body.ToMessageBody();
			await using var rawStream = new MemoryStream();
			await mime.WriteToAsync(rawStream);

			var path = PartPath(mime);
			var accountId = Guid.NewGuid();
			context.Accounts.Add(new Account { Id = accountId, DisplayName = "Test" });
			context.Messages.Add(new Message { Id = messageId, AccountId = accountId, ReceivedAt = DateTimeOffset.UtcNow });
			context.MessageRaws.Add(new MessageRaw { MessageId = messageId, Content = rawStream.ToArray() });
			context.MessageContentStates.Add(new MessageContentState { MessageId = messageId, RawVersion = 1 });
			context.Attachments.Add(new Attachment
			{
				Id = attachmentId,
				MessageId = messageId,
				PartSpecifier = path,
				RawVersion = 1,
				Filename = "report.txt",
				MimeType = "text/plain",
				Size = payload.Length,
			});
			await context.SaveChangesAsync();

			var service = scope.ServiceProvider.GetRequiredService<AttachmentService>();
			var (_, decoded) = await service.ReadAsync(messageId, attachmentId);
			Assert.Equal(payload, decoded);
			var materialised = await service.MaterialiseForOpeningAsync(messageId, attachmentId);
			Assert.Equal(payload, await File.ReadAllBytesAsync(materialised));
			Assert.StartsWith(Path.Combine(database.Directory, "tmp", "attachments"), materialised, StringComparison.Ordinal);
			if (!OperatingSystem.IsWindows())
			{
				Assert.Equal(
					UnixFileMode.UserRead | UnixFileMode.UserWrite,
					File.GetUnixFileMode(materialised)
				);
				Assert.Equal(
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
					File.GetUnixFileMode(Path.GetDirectoryName(materialised)!)
				);
			}
		}
	}

	[Theory]
	[InlineData("../../invoice.pdf", ".._.._invoice.pdf")]
	[InlineData("CON", "attachment")]
	[InlineData("report. ", "report")]
	public void Sanitises_untrusted_filenames(string input, string expected) =>
		Assert.Equal(expected, AttachmentTempDirectory.SanitiseFilename(input));

	private static string PartPath(MimeMessage message)
	{
		var iterator = new MimeIterator(message);
		while (iterator.MoveNext())
		{
			if (iterator.Current is MimePart { IsAttachment: true })
			{
				return iterator.PathSpecifier;
			}
		}
		throw new InvalidOperationException("The test message did not contain an attachment.");
	}
}
