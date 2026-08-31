using System.Security.Cryptography;
using System.Text;

namespace MyloMail.Api.Credentials;

/// <summary>
/// Names the independent credentials one account may use. Reuse is represented by selecting
/// <see cref="Primary"/>; no password is ever copied between transports.
/// </summary>
public static class CredentialSlots
{
	public const string Primary = "primary";
	public const string CalDav = "caldav-basic";
	public const string Smtp = "smtp-basic";

	/// <summary>
	/// Derives an opaque account-local key for a secondary credential. The underlying store is
	/// still the sole durable secret boundary; this is only a namespace within it.
	/// </summary>
	public static Guid Key(Guid accountId, string slot)
	{
		if (slot == Primary)
		{
			return accountId;
		}

		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"mylomail:{accountId:N}:{slot}"));
		return new Guid(bytes[..16]);
	}

	public static Task StoreSlotAsync(
		this ICredentialStore store,
		Guid accountId,
		string slot,
		CredentialPayload payload,
		CancellationToken ct
	) => store.StoreAsync(Key(accountId, slot), payload, ct);

	public static Task<CredentialPayload?> RetrieveSlotAsync(
		this ICredentialStore store,
		Guid accountId,
		string slot,
		CancellationToken ct
	) => store.RetrieveAsync(Key(accountId, slot), ct);

	public static Task DeleteSlotAsync(
		this ICredentialStore store,
		Guid accountId,
		string slot,
		CancellationToken ct
	) => store.DeleteAsync(Key(accountId, slot), ct);
}
