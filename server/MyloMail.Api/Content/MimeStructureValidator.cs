using MimeKit;

namespace MyloMail.Api.Content;

internal static class MimeStructureValidator
{
	private const int MaximumEntities = 4096;
	private const int MaximumDepth = 32;

	public static void Validate(MimeMessage message)
	{
		var entities = 0;
		Visit(message.Body, 1);

		void Visit(MimeEntity? entity, int depth)
		{
			if (entity is null)
			{
				return;
			}
			if (depth > MaximumDepth || ++entities > MaximumEntities)
			{
				throw new InvalidOperationException("Message MIME structure exceeds the safety limit.");
			}
			if (entity is Multipart multipart)
			{
				foreach (var child in multipart)
				{
					Visit(child, depth + 1);
				}
			}
			else if (entity is MessagePart nested)
			{
				Visit(nested.Message?.Body, depth + 1);
			}
		}
	}
}
