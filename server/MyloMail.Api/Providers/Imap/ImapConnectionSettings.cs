namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Everything needed to open one IMAP session.
/// </summary>
/// <remarks>
/// <b>This carries a password, and is therefore a stand-in.</b> §4 puts credentials in an
/// OS credential store resolved at use time, never on <c>Account</c> and never in
/// configuration. When that store exists, this record keeps the non-secret fields and the
/// secret is resolved through it. Until then this exists so the provider can be exercised
/// against the local capability matrix.
/// </remarks>
public sealed record ImapConnectionSettings(
	string Host,
	int Port,
	bool UseSsl,
	string UserName,
	string Password
);
