using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MyloMail.Api.Persistence;

/// <summary>
/// Runs migrations at startup, behind a consistent backup (§9).
/// </summary>
public sealed class DatabaseBootstrapper(MyloMailDbContext context, ILogger<DatabaseBootstrapper> logger)
{
	internal const string BackupSuffix = ".premigration.bak";

	/// <summary>
	/// Backs up an existing database, migrates, and removes the backup only once migration
	/// has succeeded — so a failed migration always leaves a restorable copy.
	/// </summary>
	/// <remarks>
	/// The backup is taken with <c>VACUUM INTO</c>, never a filesystem copy: in WAL mode the
	/// <c>-wal</c> file is part of the persistent database state, so copying the main file
	/// alone can lose committed transactions or produce a corrupt copy.
	/// </remarks>
	public async Task MigrateAsync(CancellationToken cancellationToken = default)
	{
		var databasePath = context.Database.GetDbConnection().DataSource;
		var backupPath = string.IsNullOrEmpty(databasePath) ? null : databasePath + BackupSuffix;

		var backedUp = false;
		if (backupPath is not null && File.Exists(databasePath))
		{
			await BackUpAsync(backupPath, cancellationToken).ConfigureAwait(false);
			backedUp = true;
		}

		try
		{
			await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// Surfaced to the user, never silently retried: retrying a migration that
			// failed part-way is how a restorable database becomes an unrestorable one.
			if (backedUp)
			{
				logger.LogError(
					ex,
					"Database migration failed. A pre-migration backup has been kept at {BackupPath}.",
					backupPath
				);
			}

			throw;
		}

		if (backedUp)
		{
			File.Delete(backupPath!);
		}
	}

	private async Task BackUpAsync(string backupPath, CancellationToken cancellationToken)
	{
		// A leftover backup means a previous migration failed. Overwriting it silently
		// would discard the only copy predating that failure, so it is kept and named.
		if (File.Exists(backupPath))
		{
			throw new InvalidOperationException(
				$"A pre-migration backup already exists at '{backupPath}', which means an earlier "
					+ "migration failed. Restore or remove it before starting again."
			);
		}

		await context
			.Database.ExecuteSqlRawAsync(
				"VACUUM INTO $backup;",
				[new SqliteParameter("$backup", backupPath)],
				cancellationToken
			)
			.ConfigureAwait(false);

		logger.LogInformation("Pre-migration backup written.");
	}
}
