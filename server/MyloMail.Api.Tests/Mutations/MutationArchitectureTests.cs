using System.Reflection;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
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
	/// Send never resolves to a replayable policy. Its side effect is visible to someone other
	/// than the user, and no local record can distinguish "not sent" from "sent, result lost".
	/// </summary>
	[Theory]
	[InlineData(ProviderType.Gmail)]
	[InlineData(ProviderType.Microsoft365)]
	[InlineData(ProviderType.Imap)]
	public void Send_is_never_retry_safe_on_any_provider(ProviderType type)
	{
		var policy = MutationRecoveryPolicyResolver.For(MutationOperationKind.Send, Capabilities(type));

		Assert.Equal(MutationRecoveryPolicy.AmbiguousOutcome, policy);
	}

	/// <summary>
	/// Capability is a cost input, never a safety one. IMAP UIDPLUS returns the destination
	/// UID and improves the normal path, but after a crash that response is gone and IMAP
	/// cannot be asked what it previously returned.
	/// </summary>
	[Fact]
	public void A_move_is_never_retry_safe_however_capable_the_provider()
	{
		Assert.Equal(
			MutationRecoveryPolicy.ReconcileThenRetry,
			MutationRecoveryPolicyResolver.For(MutationOperationKind.MoveMessage, Capabilities(ProviderType.Gmail))
		);
	}

	private static Api.Providers.ProviderCapabilities Capabilities(ProviderType type) =>
		type switch
		{
			ProviderType.Gmail => Fakes.ProviderShapes.Gmail,
			ProviderType.Microsoft365 => Fakes.ProviderShapes.Graph,
			_ => Fakes.ProviderShapes.Imap(Api.Providers.ImapCapabilityTier.QResync),
		};

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
