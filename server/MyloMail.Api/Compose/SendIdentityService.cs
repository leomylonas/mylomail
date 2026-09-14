using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Compose;

/// <summary>
/// Send-as identity management (§1, §15). A real alias scenario — an account with more than
/// one address it can send as — needs its own display name, address and signature per
/// identity, selectable at compose time; this is where one gets added, edited, deleted, or
/// promoted to the account's default.
/// </summary>
public sealed class SendIdentityService(MyloMailDbContext context, IHubEvents events)
{
	public async Task<SendIdentity> AddAsync(
		Guid accountId,
		string displayName,
		string emailAddress,
		string? signatureHtml,
		CancellationToken ct = default
	)
	{
		RequireNonBlank(displayName, emailAddress);

		// The first identity an account ever gets is its default by construction — nothing
		// else would ever set it, and an account with no default identity has no answer to
		// "who is this account" (§1).
		var isFirst = !await context.SendIdentities.AnyAsync(i => i.AccountId == accountId, ct);
		var identity = new SendIdentity
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			DisplayName = displayName,
			EmailAddress = emailAddress,
			SignatureHtml = signatureHtml,
			IsDefault = isFirst,
		};
		context.SendIdentities.Add(identity);
		await context.SaveChangesAsync(ct);
		await events.SendIdentitiesChangedAsync(accountId);
		return identity;
	}

	public async Task<SendIdentity> UpdateAsync(
		Guid identityId,
		string displayName,
		string emailAddress,
		string? signatureHtml,
		CancellationToken ct = default
	)
	{
		RequireNonBlank(displayName, emailAddress);

		var identity = await context.SendIdentities.FirstAsync(i => i.Id == identityId, ct);
		identity.DisplayName = displayName;
		identity.EmailAddress = emailAddress;
		identity.SignatureHtml = signatureHtml;
		await context.SaveChangesAsync(ct);
		await events.SendIdentitiesChangedAsync(identity.AccountId);
		return identity;
	}

	/// <summary>
	/// Mirrors the client's own disabled-Save-button gate (blank display name or address)
	/// server-side. A direct hub call — or a race with a not-yet-hydrated form — would
	/// otherwise create or update an identity with a blank <c>EmailAddress</c>, which
	/// downstream code treats as a real address: it's matched against inbound "From" headers
	/// during draft materialisation and handed to the provider as the outgoing "From" at send
	/// time, where a blank/unparseable address fails as an unexpected exception rather than a
	/// clean rejection.
	/// </summary>
	/// <exception cref="InvalidOperationException">Either field is blank.</exception>
	private static void RequireNonBlank(string displayName, string emailAddress)
	{
		if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(emailAddress))
		{
			throw new InvalidOperationException(
				"A send-as identity needs both a display name and an email address."
			);
		}
	}

	/// <summary>
	/// Promotes one identity to the account's default without ever committing an account with
	/// no default identity.
	/// </summary>
	public async Task<SendIdentity> SetDefaultAsync(Guid identityId, CancellationToken ct = default)
	{
		var identity = await context.SendIdentities.FirstAsync(i => i.Id == identityId, ct);
		if (identity.IsDefault)
		{
			return identity;
		}

		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		await context
			.SendIdentities.Where(i => i.AccountId == identity.AccountId && i.IsDefault)
			.ExecuteUpdateAsync(update => update.SetProperty(i => i.IsDefault, false), ct);
		await context
			.SendIdentities.Where(i => i.Id == identityId)
			.ExecuteUpdateAsync(update => update.SetProperty(i => i.IsDefault, true), ct);
		await transaction.CommitAsync(ct);

		await context.Entry(identity).ReloadAsync(ct);
		await events.SendIdentitiesChangedAsync(identity.AccountId);
		return identity;
	}

	/// <exception cref="InvalidOperationException">
	/// The identity is the account's default (an account must always have one — promote
	/// another identity first), or a saved draft still points at it (the foreign key is
	/// <c>Restrict</c>, not <c>Cascade</c>, precisely so a draft never silently loses the
	/// identity it was written from).
	/// </exception>
	public async Task DeleteAsync(Guid identityId, CancellationToken ct = default)
	{
		var identity = await context.SendIdentities.FirstAsync(i => i.Id == identityId, ct);
		if (identity.IsDefault)
		{
			throw new InvalidOperationException(
				"The default identity can't be deleted — make another identity the default first."
			);
		}

		var referencedByDraft = await context.Drafts.AnyAsync(d => d.SendIdentityId == identityId, ct);
		if (referencedByDraft)
		{
			throw new InvalidOperationException(
				"This identity is used by a saved draft and can't be deleted until that draft is sent, deleted, or reassigned to another identity."
			);
		}

		context.SendIdentities.Remove(identity);
		await context.SaveChangesAsync(ct);
		await events.SendIdentitiesChangedAsync(identity.AccountId);
	}
}
