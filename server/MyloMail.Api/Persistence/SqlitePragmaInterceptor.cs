using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MyloMail.Api.Persistence;

/// <summary>
/// Applies the required pragmas to <b>every</b> connection (§9). Only
/// <c>journal_mode</c> persists on the file; the rest are per-connection and are silently
/// back at their defaults on any connection that skips this.
/// </summary>
/// <remarks>
/// <para>
/// <c>synchronous=FULL</c> is not a tuning choice. The dispatch boundary (§6) defines
/// durable as surviving OS-level power loss, and the commonly recommended
/// <c>synchronous=NORMAL</c> explicitly trades exactly that for speed.
/// </para>
/// </remarks>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
	/// <summary>The pragmas, in the order they are applied. Public so tests can assert against the same list.</summary>
	public static readonly IReadOnlyList<(string Pragma, string Expected)> Pragmas =
	[
		("journal_mode", "wal"),
		("synchronous", "2"),
		("busy_timeout", "5000"),
		("foreign_keys", "1"),
	];

	private const string PragmaSql = """
		PRAGMA journal_mode=WAL;
		PRAGMA synchronous=FULL;
		PRAGMA busy_timeout=5000;
		PRAGMA foreign_keys=ON;
		""";

	public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
	{
		Apply(connection);
		base.ConnectionOpened(connection, eventData);
	}

	public override async Task ConnectionOpenedAsync(
		DbConnection connection,
		ConnectionEndEventData eventData,
		CancellationToken cancellationToken = default
	)
	{
		await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
		await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
	}

	private static void Apply(DbConnection connection)
	{
		using var command = connection.CreateCommand();
		command.CommandText = PragmaSql;
		command.ExecuteNonQuery();
	}

	private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = PragmaSql;
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
