using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Content;

/// <summary>
/// Reads received attachments from their canonical raw MIME, and materialises a private,
/// short-lived copy only when the operating system needs a path to open it (§9).
/// </summary>
public sealed class AttachmentService(MyloMailDbContext context, AttachmentTempDirectory temp)
{
	public async Task<IReadOnlyList<Attachment>> ListAsync(Guid messageId, CancellationToken ct = default) =>
		await context.Attachments.Where(a => a.MessageId == messageId).OrderBy(a => a.Filename).ToListAsync(ct);

	public async Task<(Attachment Attachment, byte[] Content)> ReadAsync(
		Guid messageId,
		Guid attachmentId,
		CancellationToken ct = default
	)
	{
		var attachment = await context.Attachments.FirstOrDefaultAsync(
			a => a.Id == attachmentId && a.MessageId == messageId,
			ct
		) ?? throw new FileNotFoundException("The attachment is no longer available.");
		var raw = await context.MessageRaws.FirstOrDefaultAsync(r => r.MessageId == messageId, ct)
			?? throw new FileNotFoundException("The message content has not been fetched.");
		var state = await context.MessageContentStates.FirstOrDefaultAsync(s => s.MessageId == messageId, ct);
		if (state?.RawVersion != attachment.RawVersion)
		{
			throw new FileNotFoundException("The attachment metadata is stale.");
		}

		using var rawStream = new MemoryStream(raw.Content);
		var mime = await MimeMessage.LoadAsync(rawStream, ct);
		MimeStructureValidator.Validate(mime);
		var iterator = new MimeIterator(mime);
		while (iterator.MoveNext())
		{
			if (iterator.PathSpecifier != attachment.PartSpecifier || iterator.Current is not MimePart part)
			{
				continue;
			}

			if (part.Content is null)
			{
				throw new FileNotFoundException("The attachment has no content.");
			}

			using var decoded = new MemoryStream();
			await part.Content.DecodeToAsync(decoded, ct);
			return (attachment, decoded.ToArray());
		}

		throw new FileNotFoundException("The attachment part is no longer available.");
	}

	public async Task<string> MaterialiseForOpeningAsync(Guid messageId, Guid attachmentId, CancellationToken ct = default)
	{
		var (attachment, content) = await ReadAsync(messageId, attachmentId, ct);
		return await temp.WriteAsync(attachment.Filename, content, ct);
	}
}

/// <summary>Owns the app-private attachment directory and its startup/shutdown cleanup (§9).</summary>
public sealed class AttachmentTempDirectory
{
	private readonly string root;

	public AttachmentTempDirectory(string dataDirectory) => root = Path.Combine(dataDirectory, "tmp", "attachments");

	public void Cleanup()
	{
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	public async Task<string> WriteAsync(string filename, byte[] content, CancellationToken ct)
	{
		var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(directory);
		}
		else
		{
			Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		var safeName = SanitiseFilename(filename);
		var path = Path.GetFullPath(Path.Combine(directory, safeName));
		var fullDirectory = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
		if (!path.StartsWith(fullDirectory, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("The attachment filename escaped its private directory.");
		}

		await using var stream = OperatingSystem.IsWindows()
			? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true)
			: new FileStream(OpenPrivateFile(path), FileAccess.Write, bufferSize: 4096, isAsync: false);
		await stream.WriteAsync(content, ct);
		return path;
	}

	internal static string SanitiseFilename(string filename)
	{
		var candidate = new string(filename.Where(c => !char.IsControl(c)).ToArray());
		candidate = candidate.Replace('/', '_').Replace('\\', '_').Trim().TrimEnd('.', ' ');
		if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or ".." || IsWindowsDeviceName(candidate))
		{
			return "attachment";
		}
		return candidate;
	}

	private static bool IsWindowsDeviceName(string value)
	{
		var stem = value.Split('.')[0].ToUpperInvariant();
		return stem is "CON" or "PRN" or "AUX" or "NUL"
			|| (stem.StartsWith("COM", StringComparison.Ordinal) && stem.Length == 4 && stem[3] is >= '1' and <= '9')
			|| (stem.StartsWith("LPT", StringComparison.Ordinal) && stem.Length == 4 && stem[3] is >= '1' and <= '9');
	}

	// The managed API in the pinned .NET 8 runtime has no create-with-mode overload for an
	// asynchronous FileStream. libc's open creates the inode at 0600, avoiding the brief
	// world-readable window that a subsequent chmod would introduce (§9).
	private static SafeFileHandle OpenPrivateFile(string path)
	{
		const int writeOnly = 1;
		var flags = OperatingSystem.IsMacOS()
			? writeOnly | 0x200 | 0x800 | 0x1000000
			: writeOnly | 0x40 | 0x80 | 0x80000;
		var descriptor = open(path, flags, 0x180);
		if (descriptor < 0)
		{
			throw new IOException($"Could not create private attachment file: {Marshal.GetLastPInvokeError()}");
		}
		return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
	}

	[DllImport("libc", SetLastError = true, EntryPoint = "open")]
	private static extern int open(string path, int flags, uint mode);
}
