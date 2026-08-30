using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Resolved before the database path is known, and therefore before anything in AppSettings
// can be read (§15).
var dataDirectory = DataDirectory.Resolve(BootstrapConfig.Load().DataDirectoryOverride);
builder.Services.AddPersistence(dataDirectory);
builder.Services.AddMutations();

var app = builder.Build();

// Migration runs at startup, behind a VACUUM INTO backup, and surfaces failure rather than
// retrying (§9).
await using (var scope = app.Services.CreateAsyncScope())
{
	await scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>().MigrateAsync();

	// With in-memory job storage this is the sole recovery mechanism, not a safety net
	// behind a durable queue (§6).
	var reconciliation = scope.ServiceProvider.GetRequiredService<StartupReconciliation>();
	await reconciliation.ReleaseOrphanedLeasesAsync();
	await reconciliation.FindAsync();
}

await app.RunAsync();
