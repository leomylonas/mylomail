using Microsoft.Graph.Models;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// Graph's delta payload exposes only a coarse HasAttachments value. It cannot establish the
/// non-inline state from raw MIME, so it must leave existing MIME-derived metadata untouched.
/// </summary>
public sealed class GraphMessageMappingTests
{
	[Fact]
	public void A_coarse_Graph_attachment_flag_is_not_an_authoritative_non_inline_observation()
	{
		var dto = GraphMailProvider.ToDto(new Message
		{
			Id = "message-id",
			HasAttachments = true,
		});

		Assert.Null(dto.HasNonInlineAttachments);
	}
}
