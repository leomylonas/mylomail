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
