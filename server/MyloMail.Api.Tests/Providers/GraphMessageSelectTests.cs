using Microsoft.Graph.Models;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphMessageSelectTests
{
	/// <summary>
	/// Graph answers a <c>$select</c> naming a property that does not exist on the message type
	/// with HTTP 400, which failed every folder's first sync ("Could not find a property named
	/// 'inReplyTo'"). The SDK's generated model lists exactly the properties the service
	/// defines, so checking against it catches that offline.
	/// </summary>
	[Fact]
	public void Every_selected_property_exists_on_the_graph_message_model()
	{
		var defined = new Message().GetFieldDeserializers().Keys.ToHashSet(StringComparer.Ordinal);

		Assert.All(GraphMailProvider.MessageSelect, property => Assert.Contains(property, defined));
	}
}
