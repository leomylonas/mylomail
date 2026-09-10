namespace MyloMail.Api.Providers;

/// <summary>Copies an untrusted provider stream without allowing it to grow an unbounded buffer.</summary>
internal static class BoundedContentReader
{
	public static async Task<byte[]> ReadAsync(Stream source, int? maximumBytes, CancellationToken ct)
	{
		using var buffer = new MemoryStream();
		var chunk = new byte[81920];
		while (true)
		{
			var read = await source.ReadAsync(chunk, ct);
			if (read == 0)
			{
				return buffer.ToArray();
			}
			if (maximumBytes is { } maximum && buffer.Length + read > maximum)
			{
				throw new InvalidOperationException($"Provider content exceeds the {maximum}-byte limit.");
			}
			buffer.Write(chunk, 0, read);
		}
	}
}
