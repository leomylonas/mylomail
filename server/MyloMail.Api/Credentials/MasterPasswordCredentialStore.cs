using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Credentials;

/// <summary>SQLite vault used only when the operating system has no usable secret store.</summary>
public sealed class MasterPasswordCredentialStore : ICredentialStore, IDisposable
{
	private const int SaltLength = 32;
	private const int NonceLength = 12;
	private const int TagLength = 16;
	private const int Iterations = 600_000;
	private static readonly byte[] VerifierPlaintext = "MyloMail credential vault verifier v1"u8.ToArray();
	private readonly MyloMailDbContext database;
	private byte[] key;

	public MasterPasswordCredentialStore(MyloMailDbContext database, byte[] key)
	{
		this.database = database;
		this.key = key;
	}

	public async Task InitializeAsync(string password, CancellationToken ct)
	{
		var settings = await database.CredentialFallbackSettings.SingleOrDefaultAsync(ct);
		if (settings is null)
		{
			var salt = RandomNumberGenerator.GetBytes(SaltLength);
			ReplaceKey(DeriveKey(password, salt));
			var verifier = Encrypt(VerifierPlaintext);
			database.CredentialFallbackSettings.Add(new CredentialFallbackSettings
			{
				Salt = salt,
				VerifierNonce = verifier.Nonce,
				VerifierCiphertext = verifier.Ciphertext,
				VerifierTag = verifier.Tag
			});
			await database.SaveChangesAsync(ct);
			return;
		}

		ReplaceKey(DeriveKey(password, settings.Salt));
		try
		{
			var verifier = Decrypt(settings.VerifierNonce, settings.VerifierCiphertext, settings.VerifierTag);
			if (!CryptographicOperations.FixedTimeEquals(verifier, VerifierPlaintext))
				throw new CredentialStoreUnavailableException("The supplied master password cannot unlock the credential vault.");
			CryptographicOperations.ZeroMemory(verifier);
		}
		catch (CryptographicException exception)
		{
			throw new CredentialStoreUnavailableException("The supplied master password cannot unlock the credential vault.", exception);
		}
	}

	public async Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct)
	{
		var encrypted = Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload));
		var existing = await database.EncryptedCredentials.SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
		if (existing is null)
		{
			database.EncryptedCredentials.Add(new EncryptedCredential { AccountId = accountId, Nonce = encrypted.Nonce, Ciphertext = encrypted.Ciphertext, Tag = encrypted.Tag });
		}
		else
		{
			existing.Nonce = encrypted.Nonce;
			existing.Ciphertext = encrypted.Ciphertext;
			existing.Tag = encrypted.Tag;
		}
		await database.SaveChangesAsync(ct);
	}

	public async Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct)
	{
		var encrypted = await database.EncryptedCredentials.SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
		if (encrypted is null) return null;
		return JsonSerializer.Deserialize<CredentialPayload>(Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag));
	}

	public async Task DeleteAsync(Guid accountId, CancellationToken ct)
	{
		var encrypted = await database.EncryptedCredentials.SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
		if (encrypted is null) return;
		database.EncryptedCredentials.Remove(encrypted);
		await database.SaveChangesAsync(ct);
	}

	public void Dispose() => CryptographicOperations.ZeroMemory(key);

	public byte[] CreateKeyCopy() => [.. key];

	private static byte[] DeriveKey(string password, byte[] salt) =>
		Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

	private (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] plaintext)
	{
		var nonce = RandomNumberGenerator.GetBytes(NonceLength);
		var ciphertext = new byte[plaintext.Length];
		var tag = new byte[TagLength];
		using var aes = new AesGcm(key, TagLength);
		aes.Encrypt(nonce, plaintext, ciphertext, tag);
		return (nonce, ciphertext, tag);
	}

	private byte[] Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag)
	{
		var plaintext = new byte[ciphertext.Length];
		using var aes = new AesGcm(key, TagLength);
		aes.Decrypt(nonce, ciphertext, tag, plaintext);
		return plaintext;
	}

	private void ReplaceKey(byte[] replacement)
	{
		CryptographicOperations.ZeroMemory(key);
		key = replacement;
	}
}
