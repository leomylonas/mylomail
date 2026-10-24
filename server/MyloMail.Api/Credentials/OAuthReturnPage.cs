namespace MyloMail.Api.Credentials;

/// <summary>
/// The page the browser shows after a sign-in, the same for Google and Microsoft.
/// </summary>
/// <remarks>
/// Both libraries hand these strings to the loopback listener, and one of them treats the failure
/// text as a format string, so the markup carries no braces at all: styling is inline, and colour
/// follows the browser's light or dark scheme through system colours rather than a media query.
/// </remarks>
internal static class OAuthReturnPage
{
	/// <summary>Shown whatever the outcome, for libraries that offer one page only; the app reports the result.</summary>
	public static readonly string Neutral = Page(
		"Return to MyloMail",
		"This step is finished. You can close this tab and go back to the app, which will tell you how it went."
	);

	public static readonly string Success = Page(
		"Return to MyloMail",
		"This step is finished. You can close this tab and go back to the app."
	);

	/// <summary>Takes the library's <c>{0}</c> (error) and <c>{1}</c> (description) placeholders.</summary>
	public static readonly string Failure = Page(
		"Sign-in did not finish",
		"Go back to MyloMail and try again. Details: {0} {1}"
	);

	private static string Page(string heading, string body) =>
		$"""
		<!DOCTYPE html>
		<html lang="en">
		<head>
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<meta name="color-scheme" content="light dark">
		<title>MyloMail</title>
		</head>
		<body style="margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:Canvas;color:CanvasText;font-family:'IBM Plex Sans',system-ui,-apple-system,'Segoe UI',sans-serif;">
		<main style="max-width:26rem;padding:2rem;text-align:center;">
		<svg xmlns="http://www.w3.org/2000/svg" viewBox="10 18 80 62" width="72" height="56" role="img" aria-label="MyloMail"><path fill="currentColor" d="M10 80V24Q10 18 16 18H25L46 44V80Z"/><path fill="#0f62fe" d="M54 80V44L75 18H84Q90 18 90 24V80Z"/></svg>
		<h1 style="margin:1.5rem 0 .75rem;font-size:1.75rem;font-weight:400;">{heading}</h1>
		<p style="margin:0;font-size:1rem;line-height:1.5;opacity:.75;">{body}</p>
		<div style="margin:2rem auto 0;width:3rem;height:3px;background:#0f62fe;"></div>
		</main>
		</body>
		</html>
		""";
}
