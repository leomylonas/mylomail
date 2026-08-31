namespace MyloMail.Api.Domain;

/// <summary>Authenticated ciphertext for one account's provider credential.</summary>
public sealed class EncryptedCredential
{
	public Guid AccountId { get; set; }
	public required byte[] Nonce { get; set; }
	public required byte[] Ciphertext { get; set; }
	public required byte[] Tag { get; set; }
}
