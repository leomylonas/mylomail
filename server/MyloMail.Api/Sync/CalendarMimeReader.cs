using System.Text;
using MimeKit;

namespace MyloMail.Api.Sync;

/// <summary>Reads a calendar MIME part without allowing a message to allocate an unbounded iCalendar document.</summary>
internal static class CalendarMimeReader
{
	public const int MaximumDecodedBytes = 1024 * 1024;

	public static async Task<string?> TryReadAsync(MimePart part, CancellationToken ct)
	{
		var content = part.Content;
		if (content is null)
		{
			return null;
		}

		try
		{
			using var stream = new BoundedMemoryStream(MaximumDecodedBytes);
			await content.DecodeToAsync(stream, ct);
			return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
		}
		catch (CalendarPartTooLargeException)
		{
			return null;
		}
	}

	private sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
	{
		private void EnsureCapacity(int count)
		{
			if (Length > maximumBytes - count)
			{
				throw new CalendarPartTooLargeException();
			}
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			EnsureCapacity(count);
			base.Write(buffer, offset, count);
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			EnsureCapacity(buffer.Length);
			base.Write(buffer);
		}

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			Write(buffer, offset, count);
			return Task.CompletedTask;
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
		{
			ct.ThrowIfCancellationRequested();
			Write(buffer.Span);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class CalendarPartTooLargeException : Exception;
}
