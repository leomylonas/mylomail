using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// The persisted sender/domain remote-content allow and block rules (§13 Epic 5).
/// Exact sender rules take precedence over their domain rule in the renderer. Every
/// unmatched sender remains blocked by default.
/// </summary>
[ApiController]
[Route("remote-content/rules")]
public class RemoteContentController(MyloMailDbContext context, IHubEvents events) : ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<RemoteContentRuleDto>>> Get(CancellationToken ct) =>
		Ok(
			await context.RemoteContentRules
				.OrderBy(x => x.Scope)
				.ThenBy(x => x.Value)
				.Select(x => new RemoteContentRuleDto(x.Id, x.Scope, x.Decision, x.Value))
				.ToListAsync(ct)
		);

	[HttpPut]
	public async Task<IActionResult> Put(PutRemoteContentRuleRequest request, CancellationToken ct)
	{
		if (!Enum.IsDefined(request.Scope) || !Enum.IsDefined(request.Decision))
		{
			return this.MutationProblem("The remote-content scope or decision is invalid.", statusCode: StatusCodes.Status400BadRequest);
		}

		var value = Normalize(request.Scope, request.Value);
		if (value is null)
		{
			return this.MutationProblem("The remote-content rule value is invalid.", statusCode: StatusCodes.Status400BadRequest);
		}

		var changed = await context.Database.ExecuteSqlInterpolatedAsync(
			$"""
			INSERT INTO "RemoteContentRules" ("Id", "Scope", "Decision", "Value", "CreatedAt")
			VALUES (
				{Guid.NewGuid()},
				{(int)request.Scope},
				{(int)request.Decision},
				{value},
				{DateTimeOffset.UtcNow}
			)
			ON CONFLICT ("Scope", "Value") DO UPDATE
			SET "Decision" = excluded."Decision"
			WHERE "Decision" <> excluded."Decision";
			""",
			ct
		);
		if (changed != 0)
		{
			await events.RemoteContentRulesChangedAsync();
		}

		return NoContent();
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
	{
		var changed = await context.RemoteContentRules.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
		if (changed != 0)
		{
			await events.RemoteContentRulesChangedAsync();
		}
		return NoContent();
	}

	private static string? Normalize(RemoteContentRuleScope scope, string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		var value = raw.Trim().ToLowerInvariant();
		if (scope == RemoteContentRuleScope.Sender)
		{
			return MailboxAddress.TryParse(value, out var mailbox)
				&& mailbox.Address.Length <= 320
				&& mailbox.Address.IndexOf('@') > 0
				&& !mailbox.Address.EndsWith('@')
				? mailbox.Address.ToLowerInvariant()
				: null;
		}

		value = value.TrimStart('@').TrimEnd('.');
		return value.Length <= 253 && Uri.CheckHostName(value) == UriHostNameType.Dns
			? value
			: null;
	}
}
