namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// The one edit made to an exported Exchange item (a Fast Transfer Stream) so that a message created
/// from MIME stops being a draft.
/// </summary>
/// <remarks>
/// Graph always creates a message from MIME as a draft and never lets <c>PidTagMessageFlags</c> be
/// changed afterwards. The mailbox export/import API round-trips the item as an opaque stream, and
/// that property is a fixed-length 32-bit value, so flipping it inside the stream cannot disturb any
/// length field. Everything else in the stream is left exactly as Exchange wrote it.
/// </remarks>
internal static class GraphFastTransferStream
{
	/// <summary><c>PidTagMessageFlags</c> (0x0E07) of type <c>PT_LONG</c> (0x0003), little-endian.</summary>
	private static readonly byte[] MessageFlagsTag = [0x03, 0x00, 0x07, 0x0E];

	private const int Read = 0x0001;
	private const int Submitted = 0x0004;
	private const int Unsent = 0x0008;

	/// <summary>The most a plausible flags value can be (the defined bits stop at 0x1000).</summary>
	private const int LargestPlausible = 0x1FFF;

	/// <summary>
	/// Rewrites the message's flags in place: no longer unsent (a draft), marked submitted as the
	/// documented workaround does, and read or unread as the original was.
	/// </summary>
	/// <returns>
	/// False when the stream holds no property that looks like the flags of a draft; the stream is
	/// then untouched. Only a candidate whose value has the unsent bit set and is small enough to be
	/// a flags value is accepted, because the four tag bytes can also occur inside other data.
	/// </returns>
	public static bool MarkAsReceived(byte[] stream, bool isRead)
	{
		for (var index = 0; index <= stream.Length - 8; index++)
		{
			if (!stream.AsSpan(index, MessageFlagsTag.Length).SequenceEqual(MessageFlagsTag))
			{
				continue;
			}

			var valueOffset = index + MessageFlagsTag.Length;
			var flags = BitConverter.ToInt32(stream, valueOffset);
			if (flags < 0 || flags > LargestPlausible || (flags & Unsent) == 0)
			{
				continue;
			}

			flags = (flags & ~Unsent) | Submitted;
			flags = isRead ? flags | Read : flags & ~Read;
			BitConverter.GetBytes(flags).CopyTo(stream, valueOffset);
			return true;
		}

		return false;
	}
}
