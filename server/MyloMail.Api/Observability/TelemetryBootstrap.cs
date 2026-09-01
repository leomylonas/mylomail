using Microsoft.Data.Sqlite;

namespace MyloMail.Api.Observability;

/// <summary>
/// Reads just enough of <see cref="Domain.AppSettings"/> to decide whether to wire up
/// OpenTelemetry, before the host — and therefore the EF Core <c>DbContext</c> it would
/// otherwise come from — exists (§15).
/// </summary>
/// <remarks>
/// Exporter setup happens once, at <c>builder.Services.AddOpenTelemetry()</c>, which runs
/// before <c>WebApplication.Build()</c>. The DI container able to resolve
/// <c>MyloMailDbContext</c> does not exist yet at that point — the same ordering constraint
/// that keeps <c>DataDirectoryOverride</c> out of this table (§9) also applies here, one level
/// down: this table itself cannot be read the normal way until the host it configures already
/// exists. A direct, read-only <c>Microsoft.Data.Sqlite</c> connection breaks that cycle, the
/// same way the bootstrap file breaks it for the data directory.
/// <para>
/// A fresh install (no <c>app.db</c> yet, or a migration that hasn't run) reads as "telemetry
/// off" — consistent with <see cref="Domain.AppSettings.TelemetryEnabled"/>'s own default, and
/// no worse than the row simply not existing yet at any other read site.
/// </para>
/// </remarks>
public static class TelemetryBootstrap
{
	public readonly record struct Settings(bool Enabled, string? OtelEndpoint);

	public static Settings Read(string dataDirectory)
	{
		var databasePath = Persistence.DataDirectory.DatabasePath(dataDirectory);
		if (!File.Exists(databasePath))
		{
			return new Settings(false, null);
		}

		try
		{
			using var connection = new SqliteConnection(
				new SqliteConnectionStringBuilder
				{
					DataSource = databasePath,
					Mode = SqliteOpenMode.ReadOnly,
				}.ToString()
			);
			connection.Open();

			using var command = connection.CreateCommand();
			command.CommandText =
				"SELECT TelemetryEnabled, OtelEndpoint FROM AppSettings LIMIT 1;";
			using var reader = command.ExecuteReader();
			if (!reader.Read())
			{
				return new Settings(false, null);
			}

			var enabled = !reader.IsDBNull(0) && reader.GetBoolean(0);
			var endpoint = reader.IsDBNull(1) ? null : reader.GetString(1);
			return new Settings(enabled, endpoint);
		}
		catch (SqliteException)
		{
			// No AppSettings table yet (pre-migration), or the file is mid-write. Either way,
			// this is a read taken opportunistically before startup proper — not a condition
			// worth failing launch over.
			return new Settings(false, null);
		}
	}
}
