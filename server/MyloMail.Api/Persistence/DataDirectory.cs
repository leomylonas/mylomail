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
		"webdav",
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

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			EnsureFileSystemIsLocal(path, ReadDarwinFileSystemType(path));
			return;
		}

		EnsureLocalUnix(path, ReadProcMounts());
	}

	internal static void EnsureLocalUnix(
		string path,
		IEnumerable<(string Point, string Type)> mounts
	)
	{
		var mount = FindMount(path, mounts);
		if (mount is not null)
		{
			EnsureFileSystemIsLocal(path, mount.Value.Type);
		}
	}

	/// <summary>
	/// The longest mount point that is a prefix of <paramref name="path"/> — the filesystem
	/// the path actually lands on, since mounts nest.
	/// </summary>
	internal static (string Point, string Type)? FindMount(
		string path,
		IEnumerable<(string Point, string Type)> mounts
	)
	{
		var full = Path.GetFullPath(path);
		(string Point, string Type)? best = null;

		foreach (var mount in mounts)
		{
			var point = Path.GetFullPath(mount.Point);
			if (!IsUnder(full, point))
			{
				continue;
			}

			if (best is null || point.Length > best.Value.Point.Length)
			{
				best = (point, mount.Type);
			}
		}

		return best;
	}

	internal static string ReadDarwinFileSystemType(string path)
	{
		var existingPath = ResolveExistingAncestorThroughLinks(path);
		try
		{
			// On Unix the DriveInfo name may be any existing path. DriveFormat calls statfs
			// for that path, so macOS identifies the actual containing filesystem without a
			// case-sensitive/case-insensitive lexical mount-point guess.
			var drive = new DriveInfo(existingPath);
			if (!drive.IsReady)
			{
				throw LocalityUnknown(path);
			}

			var type = drive.DriveFormat;
			return string.IsNullOrWhiteSpace(type)
				? throw LocalityUnknown(path)
				: type;
		}
		catch (Exception exception)
			when (exception is IOException or UnauthorizedAccessException)
		{
			throw LocalityUnknown(path, exception);
		}
	}

	internal static string ResolveExistingAncestorThroughLinks(string path)
	{
		var full = Path.GetFullPath(path);
		var root = Path.GetPathRoot(full);
		if (string.IsNullOrEmpty(root))
		{
			throw LocalityUnknown(path);
		}

		var current = root;
		var relative = full[root.Length..];
		var segments = relative.Split(
			Path.DirectorySeparatorChar,
			StringSplitOptions.RemoveEmptyEntries
		);
		foreach (var segment in segments)
		{
			var directory = new DirectoryInfo(Path.Combine(current, segment));
			if (!directory.Exists)
			{
				return current;
			}

			current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
				?? directory.FullName;
		}

		return current;
	}

	private static void EnsureFileSystemIsLocal(string path, string type)
	{
		if (
			NetworkFilesystems.Contains(type, StringComparer.OrdinalIgnoreCase)
			|| type.Equals("macfuse", StringComparison.OrdinalIgnoreCase)
			|| type.StartsWith("macfuse_", StringComparison.OrdinalIgnoreCase)
			|| type.Equals("osxfuse", StringComparison.OrdinalIgnoreCase)
			|| type.StartsWith("osxfuse_", StringComparison.OrdinalIgnoreCase)
		)
		{
			throw NotLocal(path, type);
		}
	}

	private static IReadOnlyList<(string Point, string Type)> ReadProcMounts()
	{
		const string MountsFile = "/proc/self/mounts";
		return ReadMounts(MountsFile)
			.Select(line => line.Split(' '))
			.Where(fields => fields.Length >= 3)
			.Select(fields =>
				(
					fields[1].Replace("\\040", " ", StringComparison.Ordinal),
					fields[2]
				)
			)
			.ToArray();
	}

	private static IEnumerable<string> ReadMounts(string mountsFile)
	{
		if (!File.Exists(mountsFile))
		{
			return [];
		}

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

	private static InvalidOperationException LocalityUnknown(
		string path,
		Exception? inner = null
	) =>
		new(
			$"MyloMail could not verify that the data directory '{path}' is on local storage. "
				+ "SQLite WAL mode requires local, single-machine storage.",
			inner
		);
}
