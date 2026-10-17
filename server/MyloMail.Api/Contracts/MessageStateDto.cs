using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>
/// A message's read and flagged state as the user sees it: what they last asked for where that has
/// not reached the server yet, the server's own otherwise. For a window with no message list to read it from.
/// </summary>
[TranspilationSource]
public record MessageStateDto(bool IsRead, bool IsFlagged);
