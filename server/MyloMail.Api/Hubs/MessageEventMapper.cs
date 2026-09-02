using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Hubs;

/// <summary>Projects a message into the shape the renderer's list already uses.</summary>
internal static class MessageEventMapper
{
	public static MessageSummaryDto ToSummary(Message message) =>
		new(
			message.Id,
			message.AccountId,
			message.Subject,
			message.Snippet,
			message.From,
			message.ReceivedAt,
			message.IsRead,
			message.IsFlagged,
			message.HasNonInlineAttachments,
			// A sync-observed message has no mutation-failure context at this moment — that's
			// a user-initiated-action fact, unrelated to a provider sync page landing. The
			// renderer's own MessageReceived/Updated handler invalidates ["messages"] anyway,
			// which re-fetches the accurate value from GetMessages right after this.
			null
		);
}
