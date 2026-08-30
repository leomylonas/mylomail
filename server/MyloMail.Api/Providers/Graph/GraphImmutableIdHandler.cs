namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// The only Graph HTTP pipeline used by MyloMail. Item IDs otherwise change on a move, so
/// every request must opt in to immutable IDs before it can create or consume an occurrence
/// identifier (§2).
/// </summary>
public sealed class GraphImmutableIdHandler : DelegatingHandler
{
	protected override Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken ct
	)
	{
		request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
		return base.SendAsync(request, ct);
	}
}
