using System.Runtime.InteropServices;
using System.Text.Json;

namespace MyloMail.Api.Persistence;

/// <summary>
/// The bootstrap file (§15). It holds exactly one setting, and its own location is fixed
/// and not overridable — the data directory has to be resolved before anything stored
/// inside it can be read.
/// </summary>
public sealed class BootstrapConfig
{
	public string? DataDirectoryOverride { get; set; }

	/// <summary>The fixed, OS-conventional config location.</summary>
	public static string Path
	{
		get
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				return System.IO.Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
					"MyloMail",
					"bootstrap.json"
				);
			}

			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				return System.IO.Path.Combine(
					home,
					"Library",
					"Application Support",
					"MyloMail",
					"bootstrap.json"
				);
			}

			var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
			return System.IO.Path.Combine(
				string.IsNullOrWhiteSpace(xdg) ? System.IO.Path.Combine(home, ".config") : xdg,
				"mylomail",
				"bootstrap.json"
			);
		}
	}

	/// <summary>
	/// Reads the bootstrap file, or returns defaults when there is none. A missing file is
	/// the normal case: no initialisation file is required.
	/// </summary>
	public static BootstrapConfig Load(string? path = null)
	{
		path ??= Path;
		if (!File.Exists(path))
		{
			return new BootstrapConfig();
		}

		// A malformed bootstrap file is not recoverable by guessing: falling back to the
		// default directory would silently start against an empty database while the user's
		// real one sits at the overridden path.
		return JsonSerializer.Deserialize<BootstrapConfig>(File.ReadAllText(path), SqliteJson.Options)
			?? new BootstrapConfig();
	}
}
