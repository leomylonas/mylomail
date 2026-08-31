using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Tests.Persistence;

/// <summary>
/// A real on-disk SQLite database in a temporary data directory. These tests use a file
/// rather than <c>:memory:</c> deliberately — WAL, <c>synchronous</c> and <c>VACUUM INTO</c>
/// are the things under test, and none of them mean anything in memory.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
	private readonly ServiceProvider services;

	public TestDatabase()
	{
		Directory = Path.Combine(Path.GetTempPath(), "mylomail-tests", Guid.NewGuid().ToString("n"));
		System.IO.Directory.CreateDirectory(Directory);

		services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(Directory)
			.BuildServiceProvider();
	}

	public string Directory { get; }

	public string DatabasePath => DataDirectory.DatabasePath(Directory);

	public AsyncServiceScope CreateScope() => services.CreateAsyncScope();

	public async Task MigrateAsync()
	{
		await using var scope = CreateScope();
		await scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>().MigrateAsync();
	}

	/// <summary>
	/// Asks FTS5 whether its index still matches its content table.
	/// </summary>
	/// <remarks>
	/// Run at the end of every test using this database, because an inconsistent index is
	/// silent where it is created and only surfaces as "database disk image is malformed" on
	/// some later, unrelated write — which is exactly how the first such bug here was found:
	/// far from its cause, in code that had nothing to do with search.
	/// </remarks>
	public async Task AssertSearchIndexIsIntactAsync()
	{
		await using var scope = services.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

		if (!await context.Database.CanConnectAsync())
		{
			return;
		}

		try
		{
			await context.Database.ExecuteSqlRawAsync(
				"INSERT INTO \"MessageSearchIndex\"(\"MessageSearchIndex\") VALUES ('integrity-check');"
			);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			// No such table: this database was never migrated, which some tests do on purpose.
		}
	}

	public async ValueTask DisposeAsync()
	{
		await AssertSearchIndexIsIntactAsync();
		await services.DisposeAsync();

		// Every pooled connection has to be gone before the file can be removed on Windows.
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try
		{
			System.IO.Directory.Delete(Directory, recursive: true);
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a passing test over.
		}
	}
}
