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
			message.HasNonInlineAttachments
		);
}
