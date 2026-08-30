using System.Runtime.InteropServices;

namespace MyloMail.Api.Persistence;

/// <summary>
/// Resolves the data directory holding <c>app.db</c> and its WAL sidecars (§9).
/// </summary>
/// <remarks>
/// The override comes from the bootstrap file rather than <see cref="Domain.AppSettings"/>,
/// because it has to be resolved before that table can be located.
/// </remarks>
public static class DataDirectory
{
	public const string DatabaseFileName = "app.db";

	/// <summary>Filesystem types that cannot host the database, and why (see <see cref="EnsureLocal"/>).</summary>
	private static readonly string[] NetworkFilesystems =
	[
		"nfs",
		"nfs4",
		"cifs",
		"smbfs",
		"smb3",
		"afpfs",
		"fuse.sshfs",
		"9p",
	];

	/// <summary>
	/// The OS-standard per-user data directory, used when no override is configured. App
	/// binaries and app data are never co-located, so that overwriting an installation
	/// cannot put the database at risk (§15).
	/// </summary>
	public static string Default
	{
		get
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				return Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
					"MyloMail"
				);
			}

			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				return Path.Combine(home, "Library", "Application Support", "MyloMail");
			}

			var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
			return Path.Combine(
				string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg,
				"mylomail"
			);
		}
	}

	/// <summary>
	/// Resolves and creates the data directory, rejecting network-backed locations.
	/// </summary>
	public static string Resolve(string? overridePath)
	{
		var path = string.IsNullOrWhiteSpace(overridePath) ? Default : Path.GetFullPath(overridePath);
		EnsureLocal(path);
		Directory.CreateDirectory(path);
		return path;
	}

	public static string DatabasePath(string dataDirectory) =>
		Path.Combine(dataDirectory, DatabaseFileName);

	/// <summary>
	/// Rejects network-backed locations. WAL coordinates between processes through shared
	/// memory and assumes local single-machine access; on a network filesystem that
	/// assumption fails silently, and the failure looks like corruption rather than like a
	/// misconfiguration.
	/// </summary>
	public static void EnsureLocal(string path)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// A UNC path is network-backed by definition; a mapped drive letter is one in
			// disguise, which DriveInfo reports as a network drive.
			if (path.StartsWith(@"\\", StringComparison.Ordinal))
			{
				throw NotLocal(path, "a UNC path");
			}

			var root = Path.GetPathRoot(path);
			if (
				!string.IsNullOrEmpty(root)
				&& new DriveInfo(root).DriveType == DriveType.Network
			)
			{
				throw NotLocal(path, "a mapped network drive");
			}

			return;
		}

		var mount = FindMount(path);
		if (mount is not null && NetworkFilesystems.Contains(mount.Value.Type, StringComparer.OrdinalIgnoreCase))
		{
			throw NotLocal(path, mount.Value.Type);
		}
	}

	/// <summary>
	/// The longest mount point that is a prefix of <paramref name="path"/> — the filesystem
	/// the path actually lands on, since mounts nest.
	/// </summary>
	private static (string Point, string Type)? FindMount(string path)
	{
		const string MountsFile = "/proc/self/mounts";
		if (!File.Exists(MountsFile))
		{
			return null;
		}

		var full = Path.GetFullPath(path);
		(string Point, string Type)? best = null;

		foreach (var line in ReadMounts(MountsFile))
		{
			var fields = line.Split(' ');
			if (fields.Length < 3)
			{
				continue;
			}

			var point = fields[1].Replace("\\040", " ", StringComparison.Ordinal);
			if (!IsUnder(full, point))
			{
				continue;
			}

			if (best is null || point.Length > best.Value.Point.Length)
			{
				best = (point, fields[2]);
			}
		}

		return best;
	}

	private static IEnumerable<string> ReadMounts(string mountsFile)
	{
		// The mount table is a synthetic file; an unreadable one is not a reason to refuse
		// to start, only a reason not to make a claim about the filesystem.
		try
		{
			return File.ReadAllLines(mountsFile);
		}
		catch (IOException)
		{
			return [];
		}
		catch (UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static bool IsUnder(string path, string mountPoint) =>
		mountPoint == "/"
		|| path.Equals(mountPoint, StringComparison.Ordinal)
		|| path.StartsWith(
			mountPoint.EndsWith('/') ? mountPoint : mountPoint + "/",
			StringComparison.Ordinal
		);

	private static InvalidOperationException NotLocal(string path, string kind) =>
		new(
			$"The data directory '{path}' is on {kind}. MyloMail stores its database in SQLite WAL mode, "
				+ "which coordinates through shared memory and requires local, single-machine storage. "
				+ "Choose a directory on a local disk."
		);
}
