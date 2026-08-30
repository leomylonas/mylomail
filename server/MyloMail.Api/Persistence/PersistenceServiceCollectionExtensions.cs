using Hangfire;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Mutations;
using MyloMail.Api.Outbox;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Sync;

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

	/// <summary>
	/// Registers the mutation core (§6). The fault injector is registered with
	/// <c>TryAdd</c> so a test host can substitute one; production always gets the no-op.
	/// </summary>
	public static IServiceCollection AddMutations(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);

		// Resolved from the local database at the moment of use; never cached by a provider.
		services.TryAddScoped<IProviderMailboxResolver, DbProviderMailboxResolver>();
		services.TryAddScoped<IMailProviderFactory, MailProviderFactory>();
		services.TryAddSingleton<IFaultInjector>(NullFaultInjector.Instance);
		services.AddScoped<MutationQueue>();
		services.AddScoped<MutationClaimService>();
		services.AddScoped<MutationChainEvaluator>();
		services.AddScoped<MutationExecutor>();
		services.AddScoped<MutationReconciler>();
		services.AddScoped<StartupReconciliation>();
		services.AddScoped<OutboxService>();
		services.AddScoped<SendExecutor>();
		services.AddScoped<SendReconciler>();

		return services;
	}

	/// <summary>Registers the sync state machines (§3).</summary>
	public static IServiceCollection AddSync(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<IFaultInjector>(NullFaultInjector.Instance);
		services.AddScoped<MessageIngestor>();
		services.AddScoped<TopologySyncService>();
		services.AddScoped<CoverageService>();
		services.AddScoped<ChangeStreamService>();
		services.AddScoped<IntegrityReconciliationService>();

		return services;
	}

	/// <summary>
	/// Registers the job layer (§3, §6). Storage is in-memory by design: job persistence was
	/// a second source of truth that could disagree with the app tables, and startup
	/// reconciliation was always the real recovery mechanism.
	/// </summary>
	public static IServiceCollection AddScheduling(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.AddSingleton<AccountGate>();
		services.AddSingleton<PollRegistry>();
		services.AddSingleton<IntegrityRegistry>();
		services.AddScoped<SyncJobs>();
		services.AddScoped<MutationJobs>();
		services.AddScoped<OutboxJobs>();
		services.AddScoped<StartupScheduler>();

		services.AddHangfire(configuration =>
			configuration
				.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
				.UseSimpleAssemblyNameTypeSerializer()
				.UseRecommendedSerializerSettings()
				.UseInMemoryStorage()
		);
		services.AddHangfireServer();

		return services;
	}
}
