using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>
/// The background content download queue, as counts. Every message's body is fetched after its
/// headers, newest first, one at a time per account, so a large mailbox has a long queue; this is
/// what lets the UI say so rather than leave a message looking merely slow.
/// </summary>
/// <param name="Ready">Messages whose content is downloaded and searchable.</param>
/// <param name="Waiting">Messages queued, not yet started.</param>
/// <param name="Fetching">Messages being downloaded right now.</param>
/// <remarks>Messages given up on are in none of these; they are reported as problems.</remarks>
[TranspilationSource]
public record ContentQueueDto(int Ready, int Waiting, int Fetching);
