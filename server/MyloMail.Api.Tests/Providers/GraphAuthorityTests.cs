using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphAuthorityTests
{
	private static Account Microsoft(string? tenant) =>
		new()
		{
			ProviderType = ProviderType.Microsoft365,
			ProviderConfig = tenant is null ? null : new Microsoft365ProviderConfig { TenantId = tenant },
		};

	[Fact]
	public void An_account_without_a_tenant_uses_the_deployment_authority()
	{
		var options = new GraphClientOptions { Authority = "https://login.microsoftonline.com/organizations" };

		Assert.Equal(options.Authority, GraphAuthority.For(Microsoft(null), options));
		Assert.Equal(options.Authority, GraphAuthority.For(Microsoft(""), options));
	}

	[Fact]
	public void Each_account_resolves_its_own_tenant_from_the_same_registration()
	{
		var options = new GraphClientOptions();

		Assert.Equal("https://login.microsoftonline.com/consumers", GraphAuthority.For(Microsoft("consumers"), options));
		Assert.Equal("https://login.microsoftonline.com/contoso.com", GraphAuthority.For(Microsoft("contoso.com"), options));
	}

	[Fact]
	public void A_tenant_stays_on_the_configured_cloud()
	{
		var options = new GraphClientOptions { Authority = "https://login.microsoftonline.us/common" };

		Assert.Equal("https://login.microsoftonline.us/contoso.com", GraphAuthority.For(Microsoft("contoso.com"), options));
	}

	[Theory]
	[InlineData("common")]
	[InlineData("organizations")]
	[InlineData("consumers")]
	[InlineData("72f988bf-86f1-41af-91ab-2d7cd011db47")]
	[InlineData("contoso.com")]
	[InlineData("contoso.onmicrosoft.com")]
	public void Accepted_tenants(string tenant) => Assert.True(GraphAuthority.IsValidTenant(tenant));

	[Theory]
	[InlineData("")]
	[InlineData("contoso")]
	[InlineData("evil.com/oauth2")]
	[InlineData("contoso.com?x=1")]
	[InlineData("https://evil.com")]
	[InlineData("con toso.com")]
	[InlineData("72f988bf-86f1-41af-91ab-2d7cd011db4")]
	public void Rejected_tenants(string tenant) => Assert.False(GraphAuthority.IsValidTenant(tenant));
}
