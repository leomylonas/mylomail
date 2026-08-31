using System.Net.Http.Headers;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>WebDAV request conventions shared by CalDAV discovery and sync reports.</summary>
internal static class CalDavWebDavRequest
{
	public const string DavNamespace = "DAV:";
	public const string CalDavNamespace = "urn:ietf:params:xml:ns:caldav";

	public static HttpContent Xml(string body) => new StringContent(body, System.Text.Encoding.UTF8, "application/xml");

	public static void SetDepth(HttpRequestMessage request, string depth)
	{
		request.Headers.TryAddWithoutValidation("Depth", depth);
	}

	public static void SetIfMatch(HttpRequestMessage request, string? eTag)
	{
		if (!string.IsNullOrEmpty(eTag))
		{
			request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(eTag));
		}
	}
}
