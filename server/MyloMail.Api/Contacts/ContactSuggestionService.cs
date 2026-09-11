using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Contacts;

/// <summary>Maintains account-scoped autocomplete observations from mail addresses.</summary>
public sealed class ContactSuggestionService(MyloMailDbContext context)
{
	public async Task<bool> ObserveAsync(
		Guid accountId,
		IEnumerable<Address> addresses,
		CancellationToken ct = default
	)
	{
		var parsed = new List<(string Email, string NormalizedEmail, string DisplayName)>();
		foreach (var address in addresses)
		{
			if (!ContactEmail.TryParse(address.Email, out var email)) continue;
			parsed.Add((
				email,
				ContactService.Normalize(email),
				address.Name?.Trim() ?? string.Empty
			));
		}
		var candidates = parsed
			.GroupBy(address => address.NormalizedEmail, StringComparer.Ordinal)
			.Select(group =>
			{
				var named = group.LastOrDefault(address =>
					!string.IsNullOrWhiteSpace(address.DisplayName)
				);
				return named.Email is not null ? named : group.Last();
			})
			.ToArray();
		if (candidates.Length == 0) return false;

		var changed = false;
		foreach (var candidate in candidates)
		{
			var rows = await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "ContactSuggestions" ("AccountId", "NormalizedEmail", "Email", "DisplayName")
				VALUES ({accountId}, {candidate.NormalizedEmail}, {candidate.Email}, {candidate.DisplayName})
				ON CONFLICT ("AccountId", "NormalizedEmail") DO UPDATE SET
					"Email" = excluded."Email",
					"DisplayName" = CASE
						WHEN excluded."DisplayName" <> '' THEN excluded."DisplayName"
						ELSE "ContactSuggestions"."DisplayName"
					END
				WHERE "ContactSuggestions"."Email" <> excluded."Email"
					OR (
						excluded."DisplayName" <> ''
						AND "ContactSuggestions"."DisplayName" <> excluded."DisplayName"
					);
				""",
				ct
			);
			changed |= rows > 0;
		}
		return changed;
	}
}
