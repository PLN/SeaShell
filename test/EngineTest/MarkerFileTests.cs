using System;
using System.IO;
using Xunit;

namespace EngineTest;

public class MarkerFileTests : IDisposable
{
	private readonly string _versionDir;

	public MarkerFileTests()
	{
		_versionDir = Path.Combine(Path.GetTempPath(), $"seashell-marker-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_versionDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_versionDir, true); } catch { }
	}

	[Fact]
	public void Write_Then_Read_RoundTrips()
	{
		SeaShell.MarkerFile.Write(_versionDir, SeaShell.MarkerFile.DaemonPath, @"C:\foo\seashell-daemon.exe");
		var actual = SeaShell.MarkerFile.Read(_versionDir, SeaShell.MarkerFile.DaemonPath);
		Assert.Equal(@"C:\foo\seashell-daemon.exe", actual);
	}

	[Fact]
	public void Read_TrimsWhitespace()
	{
		var path = SeaShell.MarkerFile.GetPath(_versionDir, SeaShell.MarkerFile.DaemonPath);
		File.WriteAllText(path, "  /usr/bin/seashell-daemon  \n");
		Assert.Equal("/usr/bin/seashell-daemon", SeaShell.MarkerFile.Read(_versionDir, SeaShell.MarkerFile.DaemonPath));
	}

	[Fact]
	public void Read_EmptyContent_ReturnsEmptyString_NotNull()
	{
		// Regression: the old cleanup path wrote "" on restore. Document current behavior —
		// Read returns the trimmed content, which for an empty file is "". Callers that rely
		// on a path being present must check File.Exists(MarkerFile.Read(...)), not just null.
		var path = SeaShell.MarkerFile.GetPath(_versionDir, SeaShell.MarkerFile.DaemonPath);
		File.WriteAllText(path, "");
		Assert.Equal("", SeaShell.MarkerFile.Read(_versionDir, SeaShell.MarkerFile.DaemonPath));
		Assert.True(SeaShell.MarkerFile.Exists(_versionDir, SeaShell.MarkerFile.DaemonPath));
	}

	[Fact]
	public void Read_MissingFile_ReturnsNull()
	{
		Assert.Null(SeaShell.MarkerFile.Read(_versionDir, SeaShell.MarkerFile.DaemonPath));
		Assert.False(SeaShell.MarkerFile.Exists(_versionDir, SeaShell.MarkerFile.DaemonPath));
	}

	[Fact]
	public void Delete_RemovesFile_AndIsIdempotent()
	{
		SeaShell.MarkerFile.Write(_versionDir, SeaShell.MarkerFile.ElevatorPath, "x");
		SeaShell.MarkerFile.Delete(_versionDir, SeaShell.MarkerFile.ElevatorPath);
		Assert.False(SeaShell.MarkerFile.Exists(_versionDir, SeaShell.MarkerFile.ElevatorPath));

		// Second delete must not throw
		SeaShell.MarkerFile.Delete(_versionDir, SeaShell.MarkerFile.ElevatorPath);
	}

	[Fact]
	public void Write_CreatesVersionDirectory()
	{
		var nested = Path.Combine(_versionDir, "v0.9.9.9");
		SeaShell.MarkerFile.Write(nested, SeaShell.MarkerFile.DaemonPid, "12345");
		Assert.True(File.Exists(Path.Combine(nested, "daemon.pid")));
	}
}
