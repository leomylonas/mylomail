using System.Xml.Linq;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>Parses successful WebDAV multi-status properties without binding to a server brand.</summary>
internal static class CalDavMultiStatusParser
{
	private static readonly XNamespace Dav = CalDavWebDavRequest.DavNamespace;
	private static readonly XNamespace CalDav = CalDavWebDavRequest.CalDavNamespace;

	public static IReadOnlyList<CalDavResponse> Parse(string xml)
	{
		var document = XDocument.Parse(xml, LoadOptions.None);
		return document
			.Descendants(Dav + "response")
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
}

internal sealed record CalDavResponse(
	string Href,
	string? ETag,
	string? DisplayName,
	string? CalendarData,
	bool IsDeleted
);
