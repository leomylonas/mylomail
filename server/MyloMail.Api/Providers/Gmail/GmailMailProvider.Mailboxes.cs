using Google.Apis.Gmail.v1.Data;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// Label lifecycle (§2).
/// </summary>
/// <remarks>
/// Gmail labels are flat on the server — <see cref="GmailMailProvider.ListMailboxesAsync"/>
/// always reports <c>ParentProviderMailboxId = null</c> — but nested labels are a real, if
/// purely conventional, Gmail feature: a label's server-side <c>name</c> is a
/// <c>/</c>-delimited path, and Gmail treats "Work/Projects" as a child of "Work" for display
/// purposes only. This provider therefore rebuilds that path from the local hierarchy
/// (<see cref="IProviderMailboxResolver.LocalPath"/>) whenever a label's position changes,
/// including local synthesised nodes that have no label of their own (§1).
/// </remarks>
public sealed partial class GmailMailProvider
{
	private const char PathSeparator = '/';

	public async Task<MailboxDto> CreateMailboxAsync(
		Account account,
		string name,
		Mailbox? parent,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var fullName = parent is null ? name : $"{mailboxes.LocalPath(parent.Id, PathSeparator)}{PathSeparator}{name}";
		var created = await service.Users.Labels
			.Create(new Label { Name = fullName, LabelListVisibility = "labelShow", MessageListVisibility = "show" }, UserId)
			.ExecuteThrottleAwareAsync(ct);
		return ToDto(created);
	}

	public async Task<MailboxDto> RenameMailboxAsync(
		Account account,
		Mailbox mailbox,
		string newName,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var fullName =
			mailbox.ParentId is Guid parentId
				? $"{mailboxes.LocalPath(parentId, PathSeparator)}{PathSeparator}{newName}"
				: newName;
		var updated = await service.Users.Labels
			.Patch(new Label { Name = fullName }, UserId, ProviderMailboxId(mailbox))
			.ExecuteThrottleAwareAsync(ct);
		return ToDto(updated);
	}

	public async Task<MailboxDto> MoveMailboxAsync(
		Account account,
		Mailbox mailbox,
		Mailbox? newParent,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var fullName =
			newParent is null
				? mailbox.Name
				: $"{mailboxes.LocalPath(newParent.Id, PathSeparator)}{PathSeparator}{mailbox.Name}";
		var updated = await service.Users.Labels
			.Patch(new Label { Name = fullName }, UserId, ProviderMailboxId(mailbox))
			.ExecuteThrottleAwareAsync(ct);
		return ToDto(updated);
	}

	public async Task DeleteMailboxAsync(Account account, Mailbox mailbox, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		await service.Users.Labels.Delete(UserId, ProviderMailboxId(mailbox)).ExecuteThrottleAwareAsync(ct);
	}

	private static MailboxDto ToDto(Label label) =>
		new()
		{
			ProviderMailboxId = label.Id ?? throw new InvalidOperationException("Gmail did not return a label id."),
			// The leaf segment only — Mailbox.Name is documented as never a path (§1).
			Name = (label.Name ?? string.Empty).Split(PathSeparator)[^1],
			// Reported back to the caller purely as local-parent bookkeeping; Gmail itself has
			// no parent reference (ListMailboxesAsync always reports null for this field).
			ParentProviderMailboxId = null,
			IsSubscribed = label.LabelListVisibility != "labelHide",
		};
}
