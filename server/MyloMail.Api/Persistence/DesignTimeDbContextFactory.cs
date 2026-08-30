using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyloMail.Api.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. It points at a throwaway path: migrations are generated
/// from the model, never from a live database, and the tool must not touch the user's.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MyloMailDbContext>
{
	public MyloMailDbContext CreateDbContext(string[] args) =>
		new(
			new DbContextOptionsBuilder<MyloMailDbContext>()
				.UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), "mylomail-design-time.db")}")
				.Options
		);
}
