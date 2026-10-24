using System.Reflection;

namespace MyloMail.Api.Credentials;

/// <summary>
/// The page the browser shows after a sign-in, the same for Google and Microsoft. The markup and
/// styling live in <c>Pages/OAuthReturn.html</c>; this fills in the words.
/// </summary>
/// <remarks>
/// The browser is talking to the sign-in library's own loopback listener, not to this app's server,
/// so the page has to be handed over as a string. MSAL runs its failure text through
/// <c>string.Format</c> (<c>{0}</c> error, <c>{1}</c> description), so that one has its braces doubled.
/// </remarks>
internal static class OAuthReturnPage
{
	private const string ResourceName = "OAuthReturn.html";

	/// <summary>For libraries that offer one page whatever the outcome; the app reports the result.</summary>
	public static readonly string Neutral = Render(
		"Return to MyloMail",
		"This step is finished. You can close this tab and go back to the app, which will tell you how it went."
	);

	public static readonly string Success = Render(
		"Return to MyloMail",
		"This step is finished. You can close this tab and go back to the app."
	);

	public static readonly string Failure = Render(
			"Sign-in did not finish",
			"Go back to MyloMail and try again. Details: @@ERROR@@ @@DETAIL@@"
		)
		.Replace("{", "{{", StringComparison.Ordinal)
		.Replace("}", "}}", StringComparison.Ordinal)
		.Replace("@@ERROR@@", "{0}", StringComparison.Ordinal)
		.Replace("@@DETAIL@@", "{1}", StringComparison.Ordinal);

	private static string Render(string heading, string body)
	{
		using var stream = typeof(OAuthReturnPage).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
		using var reader = new StreamReader(stream);
		return reader
			.ReadToEnd()
			.Replace("@@HEADING@@", heading, StringComparison.Ordinal)
			.Replace("@@BODY@@", body, StringComparison.Ordinal);
	}
}
