using MailKit;
using MailKit.Net.Imap;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Imap;

public sealed partial class ImapMailProvider : IIdleMailProvider
{
	private static readonly TimeSpan IdleSessionLimit = TimeSpan.FromMinutes(25);
	private static readonly TimeSpan IdleDisconnectLimit = TimeSpan.FromSeconds(5);

	/// <inheritdoc />
	/// <remarks>
	/// The IDLE connection is deliberately isolated from ordinary sync sessions. A notification
	/// only says the folder <em>may</em> have changed; it does not inspect messages or touch a
	/// durable cursor, so a disconnect can lose no observations. The scheduled stream remains
	/// the recovery path.
	/// </remarks>
	public async Task WaitForMailboxChangeAsync(Account account, Mailbox mailbox, CancellationToken ct)
	{
		using var client = await ConnectAsync(ct);
		if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
		{
			if (client.IsConnected)
				await DisconnectWithTimeoutAsync(client);
			await Task.Delay(IdleSessionLimit, ct);
			return;
		}

		var providerMailboxId = mailbox.ProviderMailboxId
			?? throw new InvalidOperationException("An IMAP mailbox always has a provider id.");
		var folder = await client.GetFolderAsync(providerMailboxId, ct);
		await folder.OpenAsync(FolderAccess.ReadOnly, ct);

		using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var timeout = new CancellationTokenSource(IdleSessionLimit);
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
		EventHandler<EventArgs> changed = (_, _) => done.Cancel();
		folder.CountChanged += changed;
		folder.MessageExpunged += (_, _) => done.Cancel();
		folder.MessageFlagsChanged += (_, _) => done.Cancel();
		try
		{
			await client.IdleAsync(done.Token, limit.Token);
		}
		catch (OperationCanceledException) when (done.IsCancellationRequested && !ct.IsCancellationRequested)
		{
			// A server notification ends IDLE through DONE. It is a successful wakeup, not failure.
		}
		finally
		{
			folder.CountChanged -= changed;
			if (client.IsConnected)
			{
				await DisconnectWithTimeoutAsync(client);
			}
		}
	}
	private static async Task DisconnectWithTimeoutAsync(ImapClient client)
	{
		using var timeout = new CancellationTokenSource(IdleDisconnectLimit);
		try
		{
			await client.DisconnectAsync(true, timeout.Token);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
		}
	}
}
