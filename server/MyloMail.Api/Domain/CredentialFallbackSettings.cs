namespace MyloMail.Api.Domain;

/// <summary>
/// Singleton metadata for the master-password credential vault. It contains no password
/// or credential material: the verifier merely proves that a supplied derived key is valid.
/// </summary>
public sealed class CredentialFallbackSettings
{
	public int Id { get; set; } = 1;
	public required byte[] Salt { get; set; }
	public required byte[] VerifierNonce { get; set; }
	public required byte[] VerifierCiphertext { get; set; }
	public required byte[] VerifierTag { get; set; }
}
