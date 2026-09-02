using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Compose;

/// <summary>
/// Send-as identity management (§1, §15). A real alias scenario — an account with more than
/// one address it can send as — needs its own display name, address and signature per
/// identity, selectable at compose time; this is where one gets added, edited, deleted, or
/// promoted to the account's default.
/// </summary>
public sealed class SendIdentityService(MyloMailDbContext context)
{
	public async Task<SendIdentity> AddAsync(
		Guid accountId,
		string displayName,
		string emailAddress,
		string? signatureHtml,
		CancellationToken ct = default
	)
	{
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
		var identity = await context.SendIdentities.FirstAsync(i => i.Id == identityId, ct);
		identity.DisplayName = displayName;
		identity.EmailAddress = emailAddress;
		identity.SignatureHtml = signatureHtml;
		await context.SaveChangesAsync(ct);
		return identity;
	}

	/// <summary>
	/// Promotes one identity to the account's default, demoting whichever one held it.
	/// </summary>
	/// <remarks>
	/// Demoted and saved <b>before</b> the promotion is even applied, as two separate writes
	/// rather than one batch: SQLite's own unique partial index (exactly one default per
	/// account) is checked per statement, not deferred to commit, and EF Core does not
	/// guarantee the two UPDATEs in a single <c>SaveChangesAsync</c> apply in the order they
	/// were assigned — promoting first would transiently leave two rows satisfying the index's
	/// filter and fail with a constraint violation that has nothing to do with anything being
	/// genuinely wrong.
	/// </remarks>
	public async Task<SendIdentity> SetDefaultAsync(Guid identityId, CancellationToken ct = default)
	{
		var identity = await context.SendIdentities.FirstAsync(i => i.Id == identityId, ct);
		if (!identity.IsDefault)
		{
			var current = await context.SendIdentities.SingleOrDefaultAsync(
				i => i.AccountId == identity.AccountId && i.IsDefault,
				ct
			);
			if (current is not null)
			{
				current.IsDefault = false;
				await context.SaveChangesAsync(ct);
			}
			identity.IsDefault = true;
			await context.SaveChangesAsync(ct);
		}
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
	}
}
