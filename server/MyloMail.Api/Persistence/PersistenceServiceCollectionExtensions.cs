using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MyloMail.Api.Persistence;

public static class PersistenceServiceCollectionExtensions
{
	/// <summary>
	/// Registers the single authoritative database. The pragma interceptor is registered on
	/// the context rather than applied at startup, because the pragmas that matter are
	/// per-connection (§9).
	/// </summary>
	public static IServiceCollection AddPersistence(this IServiceCollection services, string dataDirectory)
	{
		var connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = DataDirectory.DatabasePath(dataDirectory),
			// Pooling would hand back connections whose per-connection pragmas were set by
			// the interceptor on first open; the interceptor runs on every open, so this is
			// left at the provider default and stated only to make the dependency explicit.
			Mode = SqliteOpenMode.ReadWriteCreate,
		}.ToString();

		services.AddSingleton<SqlitePragmaInterceptor>();
		services.AddDbContext<MyloMailDbContext>(
			(provider, options) =>
				options
					.UseSqlite(connectionString)
					.AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>())
		);
		services.AddScoped<DatabaseBootstrapper>();

		return services;
	}
}
