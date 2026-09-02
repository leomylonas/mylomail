using MyloMail.Api.Domain;
using Serilog.Core;
using Serilog.Events;

namespace MyloMail.Api.Logging;

/// <summary>
/// §10's "shared enricher/destructuring policy" — partially obfuscates identifying fields
/// (email addresses) so a structured-logged <see cref="Address"/> stays useful for diagnostics
/// (the domain, whether two events share a sender) without writing the address in the clear.
/// </summary>
/// <remarks>
/// Applies only to values Serilog destructures as objects (an <c>@</c>-prefixed template
/// property, e.g. <c>logger.LogInformation("Reply sent to {@Organizer}", organizer)</c>) —
/// the discipline of never interpolating a raw address string into a log call in the first
/// place is still what §10 relies on for everything else; this exists for the case where
/// logging the address's shape is genuinely useful. It cannot see a bare, non-destructured
/// <c>{Email}</c> string property — that's still on the call site to avoid, same as a body
/// or subject.
/// </remarks>
public sealed class EmailMaskingDestructuringPolicy : IDestructuringPolicy
{
	public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
	{
		if (value is not Address address)
		{
			result = null!;
			return false;
		}

		var properties = new List<LogEventProperty>
		{
			new("Name", propertyValueFactory.CreatePropertyValue(address.Name)),
			new("Email", propertyValueFactory.CreatePropertyValue(Mask(address.Email))),
		};
		result = new StructureValue(properties, "Address");
		return true;
	}

	/// <summary>
	/// Keeps the domain (useful for grouping/diagnostics — "which provider", "which
	/// organisation") and the local part's first character (distinguishes two addresses at
	/// the same domain in a log stream); everything else is redacted.
	/// </summary>
	private static string Mask(string email)
	{
		var at = email.IndexOf('@');
		if (at <= 0 || at == email.Length - 1)
		{
			// Not shaped like an address — mask it wholesale rather than pass it through.
			return "***";
		}

		return $"{email[0]}***{email[at..]}";
	}
}
