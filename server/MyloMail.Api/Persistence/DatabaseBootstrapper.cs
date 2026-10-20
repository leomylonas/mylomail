using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace MyloMail.Api.Persistence;

/// <summary>
/// Runs migrations at startup, behind a consistent backup when there is anything to migrate (§9).
/// </summary>
public sealed class DatabaseBootstrapper(
	MyloMailDbContext context,
	ILogger<DatabaseBootstrapper> logger,
	IStartupProgress progress
)
{
	internal const string BackupSuffix = ".premigration.bak";
	private const string PartialSuffix = ".partial";
	private const int PagesPerStep = 2048;
	private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Backs up an existing database, migrates, and removes the backup only once migration
	/// has succeeded — so a failed migration always leaves a restorable copy.
	/// </summary>
	/// <remarks>
	/// The backup uses SQLite's online backup API, never a filesystem copy: in WAL mode the
	/// <c>-wal</c> file is part of the persistent database state, so copying the main file
	/// alone can lose committed transactions or produce a corrupt copy. It copies pages rather
	/// than rewriting the database the way <c>VACUUM INTO</c> does, and works in steps so
	/// progress can be reported. With nothing pending there is nothing to protect, so an
	/// ordinary launch takes no backup at all.
	/// </remarks>
	public async Task MigrateAsync(CancellationToken cancellationToken = default)
	{
		var databasePath = context.Database.GetDbConnection().DataSource;
		var backupPath = string.IsNullOrEmpty(databasePath) ? null : databasePath + BackupSuffix;
		var databaseExists = backupPath is not null && File.Exists(databasePath);

		// A leftover backup means a previous migration failed, whether or not anything is
		// still pending, so it is checked first.
		if (databaseExists && File.Exists(backupPath))
		{
			logger.LogCritical(
				"A pre-migration backup already exists at {BackupPath}, so an earlier migration or backup did not finish. Startup is refused until it is restored or removed.",
				backupPath
			);
			throw new InvalidOperationException(
				$"A pre-migration backup already exists at '{backupPath}', which means an earlier "
					+ "migration failed. Restore or remove it before starting again."
			);
		}

		var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
		if (pending.Count == 0)
		{
			logger.LogInformation("Database schema is current; no migration or backup needed.");
			return;
		}

		logger.LogInformation(
			"{Count} pending migration(s), from {First} to {Last}.",
			pending.Count,
			pending[0],
			pending[^1]
		);

		var backedUp = false;
		if (databaseExists)
		{
			var backupTimer = System.Diagnostics.Stopwatch.StartNew();
			logger.LogInformation(
				"Starting pre-migration backup of {SizeBytes} bytes to {BackupPath}.",
				new FileInfo(databasePath!).Length,
				backupPath
			);
			try
			{
				await Task.Run(() => BackUp(backupPath!, cancellationToken), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				logger.LogCritical(ex, "Pre-migration backup failed after {Elapsed}; the database was not modified.", backupTimer.Elapsed);
				throw;
			}

			backedUp = true;
			logger.LogInformation("Pre-migration backup finished in {Elapsed}.", backupTimer.Elapsed);
		}

		// No percentage is knowable for a migration, but a heartbeat tells the launcher the
		// backend is working rather than hung.
		using var heartbeat = new PeriodicTimer(HeartbeatInterval);
		var beats = Task.Run(
			async () =>
			{
				while (await heartbeat.WaitForNextTickAsync().ConfigureAwait(false))
				{
					progress.Report("migrating", null);
				}
			},
			CancellationToken.None
		);
		progress.Report("migrating", null);

		var migrationTimer = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
			logger.LogInformation("Database migration finished in {Elapsed}.", migrationTimer.Elapsed);
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
		finally
		{
			heartbeat.Dispose();
			await beats.ConfigureAwait(false);
		}

		if (backedUp)
		{
			File.Delete(backupPath!);
		}
	}

	private void BackUp(string backupPath, CancellationToken cancellationToken)
	{
		// Written under another name and renamed when complete, so an interrupted backup — the
		// launcher killed, the disk full — is never mistaken for the restorable copy a failed
		// migration leaves.
		var partialPath = backupPath + PartialSuffix;
		File.Delete(partialPath);

		var source = (SqliteConnection)context.Database.GetDbConnection();
		var openedHere = source.State != System.Data.ConnectionState.Open;
		if (openedHere)
		{
			source.Open();
			SqlitePragmaInterceptor.ApplyTo(source);
		}

		try
		{
			using (var destination = new SqliteConnection($"Data Source={partialPath}"))
			{
				destination.Open();
				SqlitePragmaInterceptor.ApplyTo(destination);
				CopyPages(source, destination, cancellationToken);
			}

			// Pooled handles would otherwise keep the file open when it is moved.
			SqliteConnection.ClearAllPools();
			File.Move(partialPath, backupPath);
		}
		catch
		{
			SqliteConnection.ClearAllPools();
			File.Delete(partialPath);
			throw;
		}
		finally
		{
			if (openedHere)
			{
				source.Close();
			}
		}

		progress.Report("backup", 1);
	}

	private void CopyPages(SqliteConnection source, SqliteConnection destination, CancellationToken cancellationToken)
	{
		var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
		if (backup is null)
		{
			throw new InvalidOperationException(
				$"Could not start the pre-migration backup: {raw.sqlite3_errmsg(destination.Handle).utf8_to_string()}"
			);
		}

		try
		{
			progress.Report("backup", 0);
			int result;
			do
			{
				cancellationToken.ThrowIfCancellationRequested();
				result = raw.sqlite3_backup_step(backup, PagesPerStep);
				var total = raw.sqlite3_backup_pagecount(backup);
				if (total > 0)
				{
					progress.Report("backup", (double)(total - raw.sqlite3_backup_remaining(backup)) / total);
				}

				if (result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
				{
					Thread.Sleep(100);
				}
			}
			while (result is raw.SQLITE_OK or raw.SQLITE_BUSY or raw.SQLITE_LOCKED);

			if (result != raw.SQLITE_DONE)
			{
				throw new InvalidOperationException($"The pre-migration backup failed (SQLite result {result}).");
			}
		}
		finally
		{
			raw.sqlite3_backup_finish(backup);
		}
	}
}
