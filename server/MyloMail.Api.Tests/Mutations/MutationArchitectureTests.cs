using System.Reflection;
using MyloMail.Api.Domain;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// The architecture test named in the conventions: <see cref="MutationItem"/> must never
/// carry a provider identifier field.
/// </summary>
public sealed class MutationArchitectureTests
{
	/// <summary>
	/// Capturing a provider identifier into durable intent preserves precisely the staleness
	/// the chain exists to prevent — an IMAP UID captured at enqueue time is wrong the moment
	/// an earlier mutation in the chain moves the message — and leaves the sequencing
	/// decorative. This is a naming check on purpose: the next such field will arrive as
	/// something called <c>ProviderMessageId</c> long before anyone reasons about why not.
	/// </summary>
	[Fact]
	public void MutationItem_carries_no_provider_identifier()
	{
		var offending = typeof(MutationItem)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase))
			.Select(p => p.Name)
			.ToList();

		Assert.Empty(offending);
	}

	/// <summary>
	/// Mailbox references in durable intent are local ids. A provider mailbox id captured
	/// here would make a folder rename invalidate queued mutations, when only deletion
	/// should.
	/// </summary>
	[Fact]
	public void Mailbox_references_in_intent_are_local_guids()
	{
		var mailboxProperties = typeof(MutationItem)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.Name.Contains("Mailbox", StringComparison.OrdinalIgnoreCase))
			.ToList();

		Assert.NotEmpty(mailboxProperties);
		Assert.All(mailboxProperties, p => Assert.Equal(typeof(Guid?), p.PropertyType));
	}

	/// <summary>
	/// <c>MessageOccurrenceRef</c> is an ephemeral execution DTO. If it ever became storable,
	/// volatile provider identity would be one property assignment away from durable intent.
	/// </summary>
	[Fact]
	public void No_persisted_entity_references_the_ephemeral_occurrence_ref()
	{
		var entities = typeof(MutationItem)
			.Assembly.GetTypes()
			.Where(t => t.Namespace == typeof(MutationItem).Namespace && t.IsClass);

		foreach (var entity in entities)
		{
			Assert.DoesNotContain(
				entity.GetProperties(BindingFlags.Public | BindingFlags.Instance),
				p => p.PropertyType == typeof(Api.Providers.Contracts.MessageOccurrenceRef)
			);
		}
	}
}
