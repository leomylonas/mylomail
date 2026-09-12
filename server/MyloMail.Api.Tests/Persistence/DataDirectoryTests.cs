using System.Runtime.InteropServices;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

public sealed class DataDirectoryTests
{
	/// <summary>
	/// WAL coordinates through shared memory and assumes local, single-machine access. On a
	/// network filesystem that assumption fails silently and looks like corruption, so the
	/// location is rejected up front (§9).
	/// </summary>
	[Fact]
	public void Rejects_a_unc_path_on_windows()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return;
		}

		Assert.Throws<InvalidOperationException>(() => DataDirectory.EnsureLocal(@"\\server\share\mylomail"));
	}

	[Fact]
	public void Rejects_the_innermost_network_mount_on_unix()
	{
		var mounts = new (string Point, string Type)[]
		{
			("/", "apfs"),
			("/Volumes/team", "smbfs"),
		};

		Assert.Throws<InvalidOperationException>(() =>
			DataDirectory.EnsureLocalUnix("/Volumes/team/MyloMail", mounts)
		);
	}

	[Theory]
	[InlineData("macfuse")]
	[InlineData("macfuse_sshfs")]
	[InlineData("osxfuse")]
	[InlineData("osxfuse_sshfs")]
	public void Rejects_macOS_FUSE_filesystem_names(string fileSystemType)
	{
		Assert.Throws<InvalidOperationException>(() =>
			DataDirectory.EnsureLocalUnix(
				"/Volumes/team/MyloMail",
				[("/", "apfs"), ("/Volumes/team", fileSystemType)]
			)
		);
	}

	[Fact]
	public void Resolves_an_intermediate_directory_symlink_before_mount_classification()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return;
		}

		var root = Path.Combine(
			Path.GetTempPath(),
			"mylomail-tests",
			Guid.NewGuid().ToString("n")
		);
		var target = Path.Combine(root, "target");
		var link = Path.Combine(root, "link");
		Directory.CreateDirectory(target);
		Directory.CreateSymbolicLink(link, target);

		try
		{
			var resolved = DataDirectory.ResolveExistingAncestorThroughLinks(
				Path.Combine(link, "not-created", "data")
			);

			Assert.Equal(target, resolved);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Darwin_mount_discovery_reports_the_current_volume()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return;
		}

		var fileSystemType = DataDirectory.ReadDarwinFileSystemType(
			Path.GetTempPath()
		);

		Assert.NotEmpty(fileSystemType);
	}

	[Fact]
	public void Accepts_a_local_path()
	{
		var path = Path.Combine(Path.GetTempPath(), "mylomail-tests", Guid.NewGuid().ToString("n"));
		DataDirectory.EnsureLocal(path);
	}

	[Fact]
	public void Resolve_creates_the_directory_and_prefers_the_override()
	{
		var path = Path.Combine(Path.GetTempPath(), "mylomail-tests", Guid.NewGuid().ToString("n"));

		Assert.Equal(path, DataDirectory.Resolve(path));
		Assert.True(Directory.Exists(path));

		Directory.Delete(path, recursive: true);
	}

	[Fact]
	public void Default_is_the_os_standard_location()
	{
		var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "mylomail" : "MyloMail";

		Assert.EndsWith(expected, DataDirectory.Default, StringComparison.Ordinal);
		Assert.True(Path.IsPathFullyQualified(DataDirectory.Default));
	}
}
