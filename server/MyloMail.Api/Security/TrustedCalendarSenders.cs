using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Security;

/// <summary>The senders the user has chosen to trust for calendar mail (<see cref="TrustedCalendarSender"/>).</summary>
public sealed class TrustedCalendarSenders(MyloMailDbContext context, TimeProvider clock)
{
	public async Task<bool> IsTrustedAsync(string address, CancellationToken ct)
	{
		var normalised = address.Trim().ToLowerInvariant();
		var at = normalised.LastIndexOf('@');
		if (at <= 0)
		{
			return false;
		}

		var domain = normalised[(at + 1)..];
		return await context.TrustedCalendarSenders.AnyAsync(
			rule =>
				(rule.Scope == TrustedCalendarSenderScope.Sender && rule.Value == normalised)
				|| (rule.Scope == TrustedCalendarSenderScope.Domain && rule.Value == domain),
			ct
		);
	}

	public async Task AddAsync(TrustedCalendarSenderScope scope, string value, CancellationToken ct)
	{
		var normalised = value.Trim().ToLowerInvariant().TrimStart('@');
		if (normalised.Length == 0 || (scope == TrustedCalendarSenderScope.Sender && !normalised.Contains('@')))
		{
			throw new ArgumentException("That is not an email address or domain.", nameof(value));
		}

		if (await context.TrustedCalendarSenders.AnyAsync(r => r.Scope == scope && r.Value == normalised, ct))
		{
			return;
		}

		context.TrustedCalendarSenders.Add(
			new TrustedCalendarSender { Id = Guid.NewGuid(), Scope = scope, Value = normalised, CreatedAt = clock.GetUtcNow() }
		);
		await context.SaveChangesAsync(ct);
	}

	public Task RemoveAsync(Guid id, CancellationToken ct) =>
		context.TrustedCalendarSenders.Where(r => r.Id == id).ExecuteDeleteAsync(ct);

	public async Task<List<TrustedCalendarSender>> ListAsync(CancellationToken ct) =>
		[.. (await context.TrustedCalendarSenders.AsNoTracking().ToListAsync(ct)).OrderBy(r => r.Value, StringComparer.Ordinal)];
}

/// <summary>
/// DKIM and DMARC first; failing that, the sender the message names, if the user has chosen to trust
/// them. A trusted sender is the same to every caller as a verified one: the user's decision stands
/// in for the DNS records the sender's domain does not publish.
/// </summary>
public sealed class TrustedSenderAuthentication(
	IIncomingMailAuthentication inner,
	TrustedCalendarSenders trusted
) : IIncomingMailAuthentication
{
	public async Task<MailAuthenticationResult> VerifyAsync(MimeMessage message, CancellationToken ct)
	{
		var result = await inner.VerifyAsync(message, ct);
		if (result.IsAuthenticated)
		{
			return result;
		}

		var from = message.From.Mailboxes.ToList();
		return from.Count == 1 && await trusted.IsTrustedAsync(from[0].Address, ct)
			? new MailAuthenticationResult(true, from[0].Address)
			: result;
	}
}
