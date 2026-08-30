using MyloMail.Api.Domain;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Resolves how an ambiguous item may be recovered, from operation type and the account
/// provider's negotiated capabilities (§6).
/// </summary>
public static class MutationRecoveryPolicyResolver
{
	/// <summary>
	/// <b>Capability is a cost input, not a safety one.</b> IMAP UIDPLUS returns the
	/// destination UID and so improves the normal path, but after a crash the returned
	/// <c>COPYUID</c> is gone and IMAP offers no way to ask what it previously returned.
	/// Nothing therefore moves from <see cref="MutationRecoveryPolicy.ReconcileThenRetry"/>
	/// to <see cref="MutationRecoveryPolicy.RetrySafe"/> on the strength of a capability —
	/// the <paramref name="capabilities"/> parameter exists to make that explicit at every
	/// call site, not to weaken the answer.
	/// </summary>
	public static MutationRecoveryPolicy For(
		MutationOperationKind operation,
		ProviderCapabilities capabilities
	) =>
		operation switch
		{
			// Absolute flag sets are idempotent by construction: each field is a value, not
			// a toggle, so replaying one converges on the same server state.
			MutationOperationKind.SetFlags => MutationRecoveryPolicy.RetrySafe,

			// Gmail label add/remove is likewise idempotent, and that is what a membership
			// removal is on a label-model provider.
			MutationOperationKind.RemoveFromMailbox when capabilities.Type == ProviderType.Gmail =>
				MutationRecoveryPolicy.RetrySafe,

			_ => MutationRecoveryPolicy.ReconcileThenRetry,
		};
}
