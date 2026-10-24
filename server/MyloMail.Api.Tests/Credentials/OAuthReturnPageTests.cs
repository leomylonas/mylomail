using System.Globalization;
using MyloMail.Api.Credentials;
using Xunit;

namespace MyloMail.Api.Tests.Credentials;

public sealed class OAuthReturnPageTests
{
	[Fact]
	public void The_failure_page_survives_the_format_call_its_library_makes_and_carries_the_error()
	{
		var shown = string.Format(CultureInfo.InvariantCulture, OAuthReturnPage.Failure, "access_denied", "The user said no");

		Assert.Contains("access_denied", shown);
		Assert.Contains("The user said no", shown);
		Assert.Contains("prefers-color-scheme", shown);
	}

	[Fact]
	public void The_other_pages_are_complete_documents_with_no_placeholder_left()
	{
		foreach (var page in new[] { OAuthReturnPage.Success, OAuthReturnPage.Neutral })
		{
			Assert.StartsWith("<!DOCTYPE html>", page);
			Assert.DoesNotContain("@@", page);
			Assert.Contains("Return to MyloMail", page);
		}
	}
}
