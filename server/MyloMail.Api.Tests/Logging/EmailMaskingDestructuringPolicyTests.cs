using MyloMail.Api.Domain;
using MyloMail.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace MyloMail.Api.Tests.Logging;

/// <summary>
/// Twenty-eighth architecture-review pass: §10 promised a Serilog pipeline with "a shared
/// enricher/destructuring policy [that] partially obfuscates identifying fields (e.g. email
/// addresses)" — this was never built. These tests are against the actual Serilog pipeline
/// (a real <see cref="Logger"/>, not a hand-rolled call to the policy), so they exercise what
/// a real log call actually produces.
/// </summary>
public sealed class EmailMaskingDestructuringPolicyTests
{
	[Fact]
	public void An_at_destructured_address_has_its_email_masked_but_keeps_the_domain()
	{
		var sink = new CapturingSink();
		using var logger = new LoggerConfiguration()
			.Destructure.With<EmailMaskingDestructuringPolicy>()
			.WriteTo.Sink(sink)
			.CreateLogger();

		logger.Information("Invited {@Organizer}", new Address("Jane Doe", "jane@example.org"));

		var evt = Assert.Single(sink.Events);
		var organizer = Assert.IsType<StructureValue>(evt.Properties["Organizer"]);
		var email = Assert.IsType<ScalarValue>(organizer.Properties.Single(p => p.Name == "Email").Value);

		Assert.Equal("j***@example.org", email.Value);
		Assert.DoesNotContain("jane@example.org", evt.RenderMessage());
	}

	[Fact]
	public void A_non_address_object_is_destructured_normally()
	{
		var sink = new CapturingSink();
		using var logger = new LoggerConfiguration()
			.Destructure.With<EmailMaskingDestructuringPolicy>()
			.WriteTo.Sink(sink)
			.CreateLogger();

		logger.Information("Count {@Count}", 5);

		var evt = Assert.Single(sink.Events);
		var value = Assert.IsType<ScalarValue>(evt.Properties["Count"]);
		Assert.Equal(5, value.Value);
	}

	private sealed class CapturingSink : ILogEventSink
	{
		public List<LogEvent> Events { get; } = [];

		public void Emit(LogEvent logEvent) => Events.Add(logEvent);
	}
}
