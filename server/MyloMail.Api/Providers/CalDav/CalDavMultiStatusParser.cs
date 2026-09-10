using System.Xml;
using System.Xml.Linq;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>Parses successful WebDAV multi-status properties without binding to a server brand.</summary>
internal static class CalDavMultiStatusParser
{
	private const int MaximumResponses = 4096;
	private const int MaximumXmlDepth = 64;

	private static readonly XNamespace Dav = CalDavWebDavRequest.DavNamespace;
	private static readonly XNamespace CalDav = CalDavWebDavRequest.CalDavNamespace;

	/// <summary>
	/// The collection-level <c>sync-token</c> a sync-collection REPORT returns alongside its
	/// responses — the next cursor, valid only once every response in this multi-status has
	/// been applied.
	/// </summary>
	public static string? SyncToken(string xml) =>
		ParseDocument(xml).Root?.Element(Dav + "sync-token")?.Value;

	public static IReadOnlyList<CalDavResponse> Parse(string xml)
	{
		var document = ParseDocument(xml);
		var responses = document.Descendants(Dav + "response").Take(MaximumResponses + 1).ToList();
		if (responses.Count > MaximumResponses)
		{
			throw new InvalidOperationException($"CalDAV multi-status exceeds the {MaximumResponses}-response limit.");
		}
		return responses
			.Select(response =>
			{
				var successful = response
					.Elements(Dav + "propstat")
					.Where(propstat => propstat.Element(Dav + "status")?.Value.Contains(" 200 ", StringComparison.Ordinal) == true)
					.Select(propstat => propstat.Element(Dav + "prop"))
					.Where(prop => prop is not null)
					.ToList();
				return new CalDavResponse(
					response.Element(Dav + "href")?.Value ?? string.Empty,
					successful.Select(prop => prop!.Element(Dav + "getetag")?.Value).FirstOrDefault(value => value is not null),
					successful.Select(prop => prop!.Element(Dav + "displayname")?.Value).FirstOrDefault(value => value is not null),
					successful.Select(prop => prop!.Element(CalDav + "calendar-data")?.Value).FirstOrDefault(value => value is not null),
					response.Element(Dav + "status")?.Value.Contains(" 404 ", StringComparison.Ordinal) == true
				);
			})
			.ToList();
	}

	private static XDocument ParseDocument(string xml)
	{
		using var reader = XmlReader.Create(
			new StringReader(xml),
			new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = xml.Length,
			}
		);
		while (reader.Read())
		{
			if (reader.Depth > MaximumXmlDepth)
			{
				throw new InvalidOperationException($"CalDAV multi-status exceeds the {MaximumXmlDepth}-level XML depth limit.");
			}
		}
		return XDocument.Parse(xml, LoadOptions.None);
	}
}

internal sealed record CalDavResponse(
	string Href,
	string? ETag,
	string? DisplayName,
	string? CalendarData,
	bool IsDeleted
);
