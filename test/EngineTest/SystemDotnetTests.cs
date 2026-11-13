using System.Collections.Generic;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

/// <summary>
/// The SYSTEM / root automatic-update entry runs as SYSTEM / root: the dotnet it
/// records must be out of reach of unprivileged users (review finding: it
/// recorded the enabling user's, possibly per-user, SDK).
/// </summary>
public class SystemDotnetTests
{
	private const string ProgramFiles = @"C:\Program Files";

	[Theory]
	[InlineData(@"C:\Program Files\dotnet\dotnet.exe", true)]
	[InlineData(@"c:\program files\dotnet\dotnet.exe", true)]
	[InlineData(@"C:\Users\qd\.dotnet\dotnet.exe", false)]                  // per-user install
	[InlineData(@"C:\Program Files (x86)\dotnet\dotnet.exe", false)]         // not under %ProgramFiles%\
	[InlineData(@"C:\Program Files\..\Users\qd\.dotnet\dotnet.exe", false)]
	public void Windows_OnlyUnderProgramFiles(string dotnet, bool qualifies) =>
		Assert.Equal(qualifies, SystemDotnet.WindowsDisqualified(dotnet, ProgramFiles) == null);

	[Fact]
	public void Windows_PerUserSdkFallsBackToTheMachineWideOne()
	{
		var candidates = SystemDotnet.Candidates(@"C:\Users\qd\.dotnet\dotnet.exe", isWindows: true, ProgramFiles);
		var choice = SystemDotnet.Choose(candidates, c => (c, SystemDotnet.WindowsDisqualified(c, ProgramFiles)));

		Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", choice.Dotnet);
		Assert.Single(choice.Rejected);
	}

	[Fact]
	public void PerUserSdkOnly_RefusesTheSystemEntry()
	{
		// No machine-wide install: the user's dotnet does not qualify, the Program
		// Files one does not exist. The SYSTEM entry must not be created.
		var candidates = SystemDotnet.Candidates(@"C:\Users\qd\.dotnet\dotnet.exe", isWindows: true, ProgramFiles);
		var choice = SystemDotnet.Choose(candidates, c => c.StartsWith(ProgramFiles)
			? (c, "does not exist")
			: (c, SystemDotnet.WindowsDisqualified(c, ProgramFiles)));

		Assert.Null(choice.Dotnet);
		Assert.Equal(2, choice.Rejected.Count);
		Assert.Contains(choice.Rejected, r => r.StartsWith(@"C:\Users\qd\.dotnet\dotnet.exe: not under C:\Program Files"));
	}

	// ── Linux ───────────────────────────────────────────────────────────

	private static readonly Dictionary<string, PosixStat> Fs = new()
	{
		["/"] = new(0, 0b111_101_101),
		["/usr"] = new(0, 0b111_101_101),
		["/usr/lib"] = new(0, 0b111_101_101),
		["/usr/lib/dotnet"] = new(0, 0b111_101_101),
		["/usr/lib/dotnet/dotnet"] = new(0, 0b111_101_101),
		["/usr/local"] = new(0, 0b010_111_111_101),        // Debian: root:staff 2775, group-writable
		["/usr/local/bin"] = new(0, 0b010_111_111_101),
		["/usr/bin"] = new(0, 0b111_101_101),
		["/bin"] = new(0, 0b111_101_101),
		["/home"] = new(0, 0b111_101_101),
		["/home/pl"] = new(1000, 0b111_101_101),
		["/home/pl/.dotnet"] = new(1000, 0b111_101_101),
		["/home/pl/.dotnet/dotnet"] = new(1000, 0b111_101_101),
		["/opt"] = new(0, 0b111_111_111),                   // world-writable
		["/opt/dotnet"] = new(0, 0b111_101_101),
		["/opt/dotnet/dotnet"] = new(0, 0b111_101_101),
	};

	private static PosixStat? Stat(string path) => Fs.TryGetValue(path, out var st) ? st : null;

	[Theory]
	[InlineData("/usr/lib/dotnet/dotnet", null)]
	[InlineData("/home/pl/.dotnet/dotnet", "/home/pl/.dotnet/dotnet is owned by uid 1000, not root")]
	[InlineData("/opt/dotnet/dotnet", "/opt is writable by others (mode 777)")]
	[InlineData("/usr/local/bin/dotnet", "/usr/local/bin/dotnet cannot be examined")]
	public void Linux_ExecutableAndEveryDirectoryAboveOwnedByRootAndNotWritable(string path, string? reason) =>
		Assert.Equal(reason, SystemDotnet.LinuxDisqualified(path, Stat));

	[Fact]
	public void Linux_RootPathIsMinimalAndChecked()
	{
		// Not the user's inherited PATH; group-writable /usr/local/bin is left out.
		Assert.Equal("/usr/lib/dotnet:/usr/bin:/bin", SystemDotnet.LinuxPathFor("/usr/lib/dotnet", Stat));
	}

	[Fact]
	public void Linux_PerUserSdkOnly_RefusesTheRootEntry()
	{
		var candidates = SystemDotnet.Candidates("/home/pl/.dotnet/dotnet", isWindows: false, "");
		Assert.Equal("/home/pl/.dotnet/dotnet", candidates[0]);
		// No machine-wide install on this machine: only the per-user one exists.
		var choice = SystemDotnet.Choose(candidates, c => (c, c.StartsWith("/home/") ? SystemDotnet.LinuxDisqualified(c, Stat) : "does not exist"));

		Assert.Null(choice.Dotnet);
		Assert.Equal(candidates.Count, choice.Rejected.Count);
	}
}
