using Microsoft.AspNetCore.Mvc;
using MyloMail.Api.Content;
using MyloMail.Api.Errors;
using MyloMail.Api.FaultInjection;

namespace MyloMail.Api.Controllers;

/// <summary>Downloads or prepares received MIME attachments without duplicating their storage.</summary>
[ApiController]
[Route("messages/{messageId:guid}/attachments")]
public sealed class MessageAttachmentsController(
	AttachmentService attachments,
	IFaultInjector faults
) : ControllerBase
{
	[HttpGet("{attachmentId:guid}")]
	public async Task<IActionResult> Download(Guid messageId, Guid attachmentId, CancellationToken ct)
	{
		try
		{
			var (attachment, content) = await attachments.ReadAsync(messageId, attachmentId, ct);
			if (faults is not NullFaultInjector)
			{
				Response.Body = new FaultInjectingResponseStream(Response.Body, faults);
			}
			return File(content, attachment.MimeType, AttachmentTempDirectory.SanitiseFilename(attachment.Filename));
		}
		catch (FileNotFoundException)
		{
			return this.MutationProblem("This attachment no longer exists.", statusCode: StatusCodes.Status404NotFound);
		}
	}

	[HttpPost("{attachmentId:guid}/open")]
	public async Task<IActionResult> PrepareForOpen(Guid messageId, Guid attachmentId, CancellationToken ct)
	{
		try
		{
			var path = await attachments.MaterialiseForOpeningAsync(messageId, attachmentId, ct);
			return Ok(new { path });
		}
		catch (FileNotFoundException)
		{
			return this.MutationProblem("This attachment no longer exists.", statusCode: StatusCodes.Status404NotFound);
		}
	}

	private sealed class FaultInjectingResponseStream(
		Stream inner,
		IFaultInjector faults
	) : Stream
	{
		private bool injected;

		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => inner.CanSeek;
		public override bool CanWrite => inner.CanWrite;
		public override long Length => inner.Length;

		public override long Position
		{
			get => inner.Position;
			set => inner.Position = value;
		}

		public override void Flush() => inner.Flush();

		public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

		public override int Read(byte[] buffer, int offset, int count) =>
			inner.Read(buffer, offset, count);

		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

		public override void SetLength(long value) => inner.SetLength(value);

		public override void Write(byte[] buffer, int offset, int count) =>
			Write(buffer.AsSpan(offset, count));

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			if (injected || buffer.Length < 2)
			{
				inner.Write(buffer);
				return;
			}

			injected = true;
			var midpoint = buffer.Length / 2;
			inner.Write(buffer[..midpoint]);
			inner.Flush();
			faults.Reached(FaultPoints.AttachmentDownloadMidTransfer);
			inner.Write(buffer[midpoint..]);
		}

		public override Task WriteAsync(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken ct
		) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

		public override async ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken ct = default
		)
		{
			if (injected || buffer.Length < 2)
			{
				await inner.WriteAsync(buffer, ct);
				return;
			}

			injected = true;
			var midpoint = buffer.Length / 2;
			await inner.WriteAsync(buffer[..midpoint], ct);
			await inner.FlushAsync(ct);
			faults.Reached(FaultPoints.AttachmentDownloadMidTransfer);
			await inner.WriteAsync(buffer[midpoint..], ct);
		}
	}
}
