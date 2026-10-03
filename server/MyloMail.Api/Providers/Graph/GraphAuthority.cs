using System.Text.RegularExpressions;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Resolves the Microsoft sign-in authority for one account. The application registration is
/// shared, but which directory signs a user in is per account: a personal Microsoft account
/// and several work tenants can be connected side by side.
/// </summary>
public static partial class GraphAuthority
{
	/// <summary>
	/// A well-known audience (<c>common</c>, <c>organizations</c>, <c>consumers</c>), a tenant
	/// GUID, or a verified domain name. No path separators or other characters, so the value
	/// can only ever select a tenant under the configured authority host.
	/// </summary>
	[GeneratedRegex(
		@"^(?:common|organizations|consumers|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}|[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+)$",
		RegexOptions.CultureInvariant
	)]
	private static partial Regex TenantPattern();

	public static bool IsValidTenant(string tenant) => TenantPattern().IsMatch(tenant);

	/// <summary>
	/// The account's own tenant under the configured authority's host (so a sovereign-cloud
	/// deployment stays on its cloud), or the deployment default when the account names none.
	/// </summary>
	public static string For(Account account, GraphClientOptions options)
	{
		if ((account.ProviderConfig as Microsoft365ProviderConfig)?.TenantId is not { Length: > 0 } tenant)
		{
			return options.Authority;
		}

		return $"{new Uri(options.Authority).GetLeftPart(UriPartial.Authority)}/{tenant}";
	}
}
