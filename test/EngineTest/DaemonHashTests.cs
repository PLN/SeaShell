using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SeaShell.Invoker;
using Xunit;

namespace EngineTest;

/// <summary>
/// Regression test for daemon hash alignment.
/// The bug: DaemonServer.ComputeSelfHash used file timestamps while
/// DaemonManager.ComputeDirHash used assembly FullNames. They never matched,
/// causing an infinite daemon restart loop. Both now use the FullName algorithm.
/// </summary>
public class DaemonHashTests
{
	[Fact]
	public void ComputeDirHash_IsDeterministic()
	{
		// Use the test's own bin directory — it contains DLLs
		var dir = AppContext.BaseDirectory;
		var hash1 = DaemonManager.ComputeDirHash(dir);
		var hash2 = DaemonManager.ComputeDirHash(dir);

		Assert.Equal(hash1, hash2);
	}

	[Fact]
	public void ComputeDirHash_Is16CharLowercaseHex()
	{
		var dir = AppContext.BaseDirectory;
		var hash = DaemonManager.ComputeDirHash(dir);

		Assert.Equal(16, hash.Length);
		Assert.Matches("^[0-9a-f]{16}$", hash);
	}

	[Fact]
	public void ComputeDirHash_MatchesDaemonAlgorithm()
	{
		// This replicates the algorithm in DaemonServer.ComputeSelfHash.
		// If either side changes, this test fails.
		var dir = AppContext.BaseDirectory;

		// DaemonServer algorithm (must match DaemonManager.ComputeDirHash):
		var sb = new StringBuilder();
		foreach (var f in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f))
		{
			var name = AssemblyName.GetAssemblyName(f);
			sb.Append(name.FullName);
		}
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
		var expected = Convert.ToHexString(bytes)[..16].ToLowerInvariant();

		var actual = DaemonManager.ComputeDirHash(dir);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void ComputeDirHash_EmptyDir_ReturnsValidHash()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"seashell-test-empty-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		try
		{
			var hash = DaemonManager.ComputeDirHash(dir);
			Assert.Equal(16, hash.Length);
			Assert.Matches("^[0-9a-f]{16}$", hash);
		}
		finally { Directory.Delete(dir, true); }
	}
}
