using System.Text.RegularExpressions;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Whose mailbox a Graph account addresses: the signed-in user's own (<c>/me</c>) or a shared
/// mailbox the signed-in user has been delegated (<c>/users/{shared}</c>). This is the single
/// seam through which the choice is made; none of the ~45 request-builder call sites in the
/// Graph providers branches on it.
/// </summary>
/// <remarks>
/// The fluent builders are written against <c>client.Me</c>, and <c>client.Users[id]</c> is a
/// distinct generated type family (own namespaces for every request body and delta response),
/// so the target is applied to the request rather than to the builder:
/// <list type="bullet">
/// <item>
/// Requests that reach the HTTP pipeline are retargeted by <see cref="GraphSharedMailboxHandler"/>
/// (<see cref="Retarget(Uri)"/>), which is only installed by <see cref="Handlers"/> for a
/// shared account. The personal pipeline is therefore exactly the pipeline that existed before
/// shared mailboxes.
/// </item>
/// <item>
/// <c>$batch</c> sub-requests never reach that pipeline: their URL is serialised into the
/// batch body when the step is added. They are retargeted at construction through
/// <see cref="Retarget(RequestInformation)"/>, next to where their <c>Prefer</c> header is set.
/// </item>
/// </list>
/// Both rewrites produce the URL the generated <c>Users[id]</c> builder would have built.
/// </remarks>
internal readonly partial record struct GraphMailbox(string? SharedMailbox)
{
	private const string MeTemplatePrefix = "{+baseurl}/me";
	private const string UserTemplatePrefix = "{+baseurl}/users/{user%2Did}";
	private const string UserPathParameter = "user%2Did";
	private const int MaximumAddressLength = 320;

	/// <summary>The signed-in user's own mailbox.</summary>
	public static GraphMailbox Personal => default;

	public bool IsShared => SharedMailbox is not null;

	public static GraphMailbox For(Account account) =>
		(account.ProviderConfig as Microsoft365ProviderConfig)?.SharedMailbox is { Length: > 0 } shared
			? new GraphMailbox(shared)
			: Personal;

	/// <summary>
	/// The Graph HTTP pipeline for an account, always beginning with the immutable-id handler
	/// (§2). A shared account additionally retargets <c>/me</c> requests and maps permission
	/// denial.
	/// </summary>
	public DelegatingHandler[] Handlers() =>
		SharedMailbox is null
			? [new GraphImmutableIdHandler()]
			: [new GraphImmutableIdHandler(), new GraphSharedMailboxHandler(this)];

	/// <summary>
	/// The shared mailbox is addressed by SMTP address; anything that is not a plain address is
	/// rejected so the value can only ever select a mailbox under <c>/users/</c>.
	/// </summary>
	public static bool IsValidSharedMailbox(string value) =>
		value.Length <= MaximumAddressLength && SharedMailboxPattern().IsMatch(value);

	[GeneratedRegex(
		@"^[A-Za-z0-9._+'&=-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$",
		RegexOptions.CultureInvariant
	)]
	private static partial Regex SharedMailboxPattern();

	/// <summary>
	/// Rewrites <c>{base}/me[/...]</c> to <c>{base}/users/{shared}[/...]</c>. Anything else —
	/// including absolute next/delta links Graph itself issued for the shared mailbox — is
	/// returned untouched.
	/// </summary>
	public Uri Retarget(Uri uri)
	{
		if (SharedMailbox is null || !uri.IsAbsoluteUri)
		{
			return uri;
		}

		var url = uri.OriginalString;
		var authority = url.IndexOf("://", StringComparison.Ordinal);
		if (authority < 0)
		{
			return uri;
		}

		var version = url.IndexOf('/', authority + 3);
		var me = version < 0 ? -1 : url.IndexOf('/', version + 1);
		if (me < 0
			|| url.AsSpan(version, me - version).IndexOfAny('?', '#') >= 0
			|| string.Compare(url, me, "/me", 0, 3, StringComparison.OrdinalIgnoreCase) != 0)
		{
			return uri;
		}

		var end = me + 3;
		if (end < url.Length && url[end] is not ('/' or '?' or '#'))
		{
			return uri;
		}

		return new Uri(
			string.Concat(url.AsSpan(0, me), "/users/", Uri.EscapeDataString(SharedMailbox), url.AsSpan(end)),
			UriKind.Absolute
		);
	}

	/// <summary>
	/// Retargets a request built from a <c>client.Me…</c> builder that will be serialised into a
	/// <c>$batch</c> body. Operates on the URL template, which is what the generated
	/// <c>Users[id]</c> builder would have produced.
	/// </summary>
	public void Retarget(RequestInformation request)
	{
		if (SharedMailbox is null
			|| request.UrlTemplate is not { } template
			|| !template.StartsWith(MeTemplatePrefix, StringComparison.Ordinal)
			|| (template.Length > MeTemplatePrefix.Length && template[MeTemplatePrefix.Length] is not ('/' or '{' or '?')))
		{
			return;
		}

		request.UrlTemplate = string.Concat(UserTemplatePrefix, template.AsSpan(MeTemplatePrefix.Length));
		request.PathParameters[UserPathParameter] = SharedMailbox;
	}
}
