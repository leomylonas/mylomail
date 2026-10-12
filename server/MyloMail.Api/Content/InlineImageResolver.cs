using MimeKit;

namespace MyloMail.Api.Content;

/// <summary>
/// Turns a message's <c>cid:</c> references into the images they name, as data URLs.
/// </summary>
/// <remarks>
/// A data URL is the part itself, so the document needs no further request — an <c>img</c>
/// request carries no bearer token, and routing parts through URLs would put remote-authored
/// Content-IDs into paths and logs. Only raster images are embedded, each bounded in size, so a
/// message cannot turn its own attachments into an arbitrarily large document or smuggle a
/// scriptable type through <c>img</c>.
/// </remarks>
public sealed class InlineImageResolver(MimeMessage? mime)
{
	public const int MaxPartBytes = 8 * 1024 * 1024;
	public const int MaxTotalBytes = 24 * 1024 * 1024;

	private static readonly HashSet<string> RasterTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		"image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/webp", "image/bmp", "image/avif",
		"image/x-icon", "image/vnd.microsoft.icon",
	};

	private readonly Dictionary<string, string?> resolved = new(StringComparer.Ordinal);
	private int totalBytes;

	public string? Resolve(string contentId)
	{
		if (mime is null)
		{
			return null;
		}

		var wanted = Normalise(contentId);
		if (resolved.TryGetValue(wanted, out var known))
		{
			return known;
		}

		return resolved[wanted] = Embed(wanted);
	}

	private string? Embed(string wanted)
	{
		MimePart? part;
		try
		{
			part = mime!
				.BodyParts.OfType<MimePart>()
				.FirstOrDefault(candidate => candidate.ContentId is not null && Normalise(candidate.ContentId) == wanted);
		}
		catch (Exception ex) when (MessageContentUnavailableException.IsMalformedMime(ex))
		{
			return null;
		}

		if (part?.Content is null || !RasterTypes.Contains(part.ContentType.MimeType))
		{
			return null;
		}

		try
		{
			using var decoded = new MemoryStream();
			part.Content.DecodeTo(decoded);
			if (decoded.Length > MaxPartBytes || totalBytes + decoded.Length > MaxTotalBytes)
			{
				return null;
			}

			totalBytes += (int)decoded.Length;
			return $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{Convert.ToBase64String(decoded.GetBuffer(), 0, (int)decoded.Length)}";
		}
		catch (Exception ex) when (MessageContentUnavailableException.IsMalformedMime(ex))
		{
			return null;
		}
	}

	/// <summary>A <c>cid:</c> URL may be percent-encoded and an id may be written with or without its angle brackets.</summary>
	private static string Normalise(string contentId)
	{
		var trimmed = contentId.Trim().Trim('<', '>');
		try
		{
			return Uri.UnescapeDataString(trimmed);
		}
		catch (UriFormatException)
		{
			return trimmed;
		}
	}
}
