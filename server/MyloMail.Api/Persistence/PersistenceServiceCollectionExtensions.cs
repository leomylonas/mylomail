using Hangfire;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyloMail.Api.Accounts;
using MyloMail.Api.Compose;
using MyloMail.Api.Content;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
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
		services.AddSingleton(new AttachmentTempDirectory(dataDirectory));
		services.AddScoped<Content.AttachmentService>();

		return services;
	}

	/// <summary>
	/// Registers the mutation core (§6). The fault injector is registered with
	/// <c>TryAdd</c> so a test host can substitute one; production always gets the no-op.
	/// </summary>
	/// <summary>
	/// Binds the application's provider client registration (§5). Supplied per deployment —
	/// via configuration or <c>Providers__Gmail__ClientId</c>-style environment variables —
	/// never committed.
	/// </summary>
	public static IServiceCollection AddProviderClients(
		this IServiceCollection services,
		IConfiguration configuration
	)
	{
		services.Configure<ProviderClientOptions>(configuration.GetSection(ProviderClientOptions.SectionName));

		// The conformance suite already defines these names, and a developer with them
		// exported should not have to set a second set to run the app. Configuration wins
		// where both are present.
		services.PostConfigure<ProviderClientOptions>(options =>
		{
			options.Gmail.ClientId ??= Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID");
			options.Gmail.ClientSecret ??= Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET");
			options.Graph.ClientId ??= Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID");

			if (Environment.GetEnvironmentVariable("GRAPH_TENANT_ID") is string tenant && tenant.Length > 0)
			{
				options.Graph.Authority = $"https://login.microsoftonline.com/{tenant}";
			}
		});

		return services;
	}

	public static IServiceCollection AddMutations(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);

		// Resolved from the local database at the moment of use; never cached by a provider.
		services.TryAddScoped<IProviderMailboxResolver, DbProviderMailboxResolver>();
		services.TryAddScoped<IMailProviderFactory, MailProviderFactory>();
		services.TryAddSingleton<IFaultInjector>(NullFaultInjector.Instance);
		services.TryAddSingleton<IMutationDispatcher, NoMutationDispatcher>();
		services.TryAddSingleton<IOutboxDispatcher, NoOutboxDispatcher>();
		services.TryAddSingleton<IDraftDispatcher, NoDraftDispatcher>();
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
		services.TryAddSingleton<IHubEvents, NoHubEvents>();
		services.AddScoped<MessageIngestor>();
		services.AddScoped<CalendarSyncService>();
		services.AddScoped<ContentAcquisition>();
		services.AddScoped<SearchIndexer>();
		services.AddScoped<MessageSearch>();
		services.AddScoped<DraftService>();
		services.AddScoped<DraftSyncService>();
		services.AddScoped<RemoteDraftMaterializer>();
		services.AddScoped<MailboxManagement>();
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

		// Replaces the no-op default, so intent enqueued by the hub is executed promptly
		// instead of waiting for the next startup sweep.
		services.AddSingleton<IMutationDispatcher, MutationDispatcher>();
		services.AddSingleton<IOutboxDispatcher, OutboxDispatcher>();
		services.AddSingleton<IDraftDispatcher, DraftDispatcher>();
		services.AddScoped<DraftJobs>();
		services.AddScoped<OutboxJobs>();
		services.AddScoped<ContentJobs>();
		services.AddScoped<StartupScheduler>();
		services.AddScoped<AccountProvisioningService>();

		services.AddHangfire(configuration =>
			configuration
				.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
				.UseSimpleAssemblyNameTypeSerializer()
				.UseRecommendedSerializerSettings()
				.UseInMemoryStorage()
		);
		return services;
	}

	/// <summary>
	/// Starts the background workers that actually run the jobs.
	/// </summary>
	/// <remarks>
	/// Deliberately separate from <see cref="AddScheduling"/>. Registering the client is
	/// harmless; starting workers is not — a test host that enqueues a job would have it
	/// executed against the test database, concurrently with the assertions, which shows up as
	/// an intermittent failure somewhere unrelated. Only the application composes both.
	/// </remarks>
	public static IServiceCollection AddSchedulingWorkers(this IServiceCollection services)
	{
		services.AddHangfireServer();
		return services;
	}
}
