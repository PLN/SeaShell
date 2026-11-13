using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class AutoUpdateTests
{
	private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
	private static readonly ScopeParts Full = ScopeParts.User | ScopeParts.Elevator | ScopeParts.System;

	// ── Task-name kinds ─────────────────────────────────────────────────

	[Theory]
	[InlineData(@"\SeaShell\SeaShell Update (qd)", "qd")]
	[InlineData(@"\SeaShell\SeaShell Update (SYSTEM)", "SYSTEM")]
	public void UpdateTask_IsNeverMatchedByStopOrCleanup(string taskPath, string target)
	{
		// InstanceStopper, the old-version cleanup and the orphan sweep match
		// daemon/elevator tasks only: ending the update task would kill the
		// update that is running the install.
		Assert.Equal(SeaShellTaskKind.Update, SeaShellTaskNames.KindOf(taskPath));
		Assert.False(SeaShellTaskNames.IsSeaShellTask(taskPath));
		Assert.False(SeaShellTaskNames.MatchesTarget(taskPath, target));
		Assert.True(SeaShellTaskNames.IsUpdateTaskOf(taskPath, target));
		Assert.Null(SeaShellTaskNames.ScriptOf(taskPath, target));
	}

	[Fact]
	public void TaskKinds_TellScriptsFromSeaShellsOwnTasks()
	{
		Assert.Equal(SeaShellTaskKind.Daemon, SeaShellTaskNames.KindOf(@"\SeaShell\SeaShell Daemon (qd) 0.6.3.1"));
		Assert.Equal(SeaShellTaskKind.Elevator, SeaShellTaskNames.KindOf(@"\SeaShell\SeaShell Elevator (qd) 0.6.3.1"));
		Assert.Equal("android-backup", SeaShellTaskNames.ScriptOf(@"\SeaShell\qd - android-backup", "qd"));
		Assert.Null(SeaShellTaskNames.ScriptOf(@"\SeaShell\other - android-backup", "qd"));  // another user's
		Assert.Equal(@"\SeaShell\SeaShell Update (qd)", SeaShellTaskNames.UpdateTaskPath("qd"));
	}

	// ── Entries ─────────────────────────────────────────────────────────

	[Fact]
	public void Windows_OneEntryPerTarget_EachUpdatingOnlyItself()
	{
		var entries = AutoUpdatePlan.Entries(Full, isWindows: true, "KUSANAGI", "qd", "s-1-5-21-1", Dotnet, source: "https://feed/index.json");

		Assert.Equal(2, entries.Count);
		var user = entries[0];
		Assert.Equal(@"\SeaShell\SeaShell Update (qd)", user.Name);
		Assert.False(user.System);
		Assert.True(user.HighestPrivileges);  // re-registers the elevator without a prompt
		Assert.Equal(new[] { "update", "--pass", "user,elevator", "--if-idle", "--quiet", "--dotnet", Dotnet, "--source", "https://feed/index.json" },
			user.Arguments);

		var system = entries[1];
		Assert.Equal(@"\SeaShell\SeaShell Update (SYSTEM)", system.Name);
		Assert.True(system.System);
		Assert.Equal(new[] { "update", "--pass", "system", "--if-idle", "--quiet", "--dotnet", Dotnet, "--source", "https://feed/index.json" },
			system.Arguments);
	}

	[Fact]
	public void Windows_UserOnlyScope_NoHighestPrivilegesAndNoSystemTask()
	{
		var entries = AutoUpdatePlan.Entries(ScopeParts.User, isWindows: true, "KUSANAGI", "qd", "s-1-5-21-1", Dotnet, source: null);

		var only = Assert.Single(entries);
		Assert.False(only.HighestPrivileges);
		Assert.DoesNotContain("--source", only.Arguments);
	}

	[Fact]
	public void DailyTime_IsStablePerMachineAndIdentity()
	{
		Assert.Equal(AutoUpdatePlan.DailyTime("KUSANAGI", "s-1-5-21-1"), AutoUpdatePlan.DailyTime("kusanagi", "S-1-5-21-1"));
		Assert.NotEqual(AutoUpdatePlan.DailyTime("KUSANAGI", "s-1-5-21-1"), AutoUpdatePlan.DailyTime("KUSANAGI", "s-1-5-18"));
	}

	[Fact]
	public void UserTaskXml_RunsTheInstalledCopyDaily_WithTheInteractivePrincipal()
	{
		var entry = AutoUpdatePlan.Entries(Full, true, "KUSANAGI", "qd", "s-1-5-21-1", Dotnet, null)[0];
		var doc = XDocument.Parse(BuildUpdateTaskXml(entry, @"C:\Users\qd\AppData\Local\seashell\current\bin\seashell.exe", "S-1-5-21-1"));
		XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

		var exec = doc.Descendants(ns + "Exec").Single();
		Assert.Equal(@"C:\Users\qd\AppData\Local\seashell\current\bin\seashell.exe", exec.Element(ns + "Command")!.Value);
		Assert.Equal($"update --pass user,elevator --if-idle --quiet --dotnet \"{Dotnet}\"", exec.Element(ns + "Arguments")!.Value);
		var principal = doc.Descendants(ns + "Principal").Single();
		Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
		Assert.Equal("HighestAvailable", principal.Element(ns + "RunLevel")!.Value);
		var trigger = doc.Descendants(ns + "CalendarTrigger").Single();
		Assert.EndsWith($"T{entry.Time:HH:mm}:00", trigger.Element(ns + "StartBoundary")!.Value);
		Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
	}

	private static string BuildUpdateTaskXml(AutoUpdateEntry entry, string exe, string sid)
	{
		var type = typeof(LegacyCleanup).Assembly.GetType("SeaShell.Installer.DaemonTasks")!;
		var method = type.GetMethod("BuildUpdateTaskXml", BindingFlags.NonPublic | BindingFlags.Static)!;
		return (string)method.Invoke(null, new object[] { entry, exe, sid })!;
	}

	// ── Linux crontab ───────────────────────────────────────────────────

	[Fact]
	public void CronLine_CarriesItsEnvironmentAndTag_AndScriptToolsIgnoreIt()
	{
		var entries = AutoUpdatePlan.Entries(ScopeParts.User | ScopeParts.System, isWindows: false, "host", "pl", "1000", "/usr/share/dotnet/dotnet", null);
		Assert.Equal(new[] { false, true }, entries.Select(e => e.System));

		var line = AutoUpdatePlan.CronLine(entries[0], "/home/pl/.local/share/seashell/current/bin/seashell",
			"/home/pl", "/usr/share/dotnet", "/usr/local/bin:/usr/bin:/bin");

		Assert.StartsWith($"{entries[0].Time.Minute} {entries[0].Time.Hour} * * * HOME=/home/pl DOTNET_ROOT=/usr/share/dotnet PATH=/usr/local/bin:/usr/bin:/bin ", line);
		Assert.Contains("seashell update --pass user --if-idle --quiet --dotnet /usr/share/dotnet/dotnet", line);
		Assert.True(AutoUpdatePlan.IsCronLine(line));
		// Outside the "# seashell:" namespace of scheduled scripts: the path refresher leaves it alone.
		Assert.Null(TaskPathRefresher.RewriteCronLine(line, "/somewhere/else/sea"));
	}

	[Fact]
	public void WithCronLine_ReplacesOnlyTheAutoUpdateLine()
	{
		var crontab = new[]
		{
			"0 4 * * * cd /home/pl && /home/pl/.local/bin/sea /home/pl/backup.cs # seashell:backup",
			"5 3 * * * HOME=/home/pl /old/seashell update --pass user --if-idle --quiet # seashell-auto-update",
			"",
		};

		var on = AutoUpdatePlan.WithCronLine(crontab, "7 2 * * * new # seashell-auto-update");
		Assert.Equal(new[] { crontab[0], "7 2 * * * new # seashell-auto-update" }, on);

		var off = AutoUpdatePlan.WithCronLine(crontab, null);
		Assert.Equal(new[] { crontab[0] }, off);
	}

	// ── Stored arguments (review findings) ──────────────────────────────

	[Theory]
	[InlineData(@"C:\feed dir\", @"""C:\feed dir\\""")]          // naive quoting: the child reads C:\feed dir"
	[InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"""C:\Program Files\dotnet\dotnet.exe""")]
	[InlineData(@"C:\feed\", @"C:\feed\")]                       // no quotes needed, backslashes literal
	[InlineData(@"a ""b"" c", @"""a \""b\"" c""")]
	[InlineData(@"x\""y", @"""x\\\""y""")]
	[InlineData("", @"""""")]
	public void TaskArguments_FollowTheWindowsCommandLineRules(string arg, string encoded) =>
		Assert.Equal(encoded, WindowsCommandLine.Quote(arg));

	[Fact]
	public void TaskArguments_ParseBackUnchanged_OnWindows()
	{
		if (!OperatingSystem.IsWindows()) return;
		var args = new[] { "update", "--source", @"C:\feed dir\", "--dotnet", @"C:\Program Files\dotnet\dotnet.exe", @"a ""q"" \\", "" };
		Assert.Equal(args, CommandLineToArgv("seashell.exe " + WindowsCommandLine.Join(args)).Skip(1));
	}

	private static string[] CommandLineToArgv(string commandLine)
	{
		var argv = CommandLineToArgvW(commandLine, out var count);
		try
		{
			return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
		}
		finally { LocalFree(argv); }
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr hMem);

	[Fact]
	public void CronLine_PercentIsNeverLiteral_SoCronDoesNotSplitTheCommand()
	{
		var entry = AutoUpdatePlan.Entries(ScopeParts.User, isWindows: false, "host", "pl", "1000", "/usr/bin/dotnet",
			"https://feed/a%20b/index.json")[0];
		var line = AutoUpdatePlan.CronLine(entry, "/home/pl/.local/share/seashell/current/bin/seashell", "/home/pl", "/usr/lib/dotnet", "/usr/bin:/bin");

		Assert.DoesNotContain("%", line);
		Assert.EndsWith(@"--source 'https://feed/a'""$(printf '\045')""'20b/index.json' # seashell-auto-update", line);
	}

	[Fact]
	public void CronLine_ShellReadsTheSourceBack_OnLinux()
	{
		if (!OperatingSystem.IsLinux()) return;
		const string source = "https://feed/a%20b/it's/index.json";
		var entry = AutoUpdatePlan.Entries(ScopeParts.User, isWindows: false, "host", "pl", "1000", "/usr/bin/dotnet", source)[0];
		var line = AutoUpdatePlan.CronLine(entry, "/bin/echo", "/home/pl", "/usr/lib/dotnet", "/usr/bin:/bin");

		// What cron hands to /bin/sh: the command after the five time fields.
		var command = string.Join(' ', line.Split(' ').Skip(5));
		var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add(command);
		using var sh = System.Diagnostics.Process.Start(psi)!;
		var output = sh.StandardOutput.ReadToEnd().Trim();
		sh.WaitForExit();
		Assert.EndsWith($"--source {source}", output);
	}

	[Fact]
	public void CronLine_RejectsLineBreaks() =>
		Assert.Throws<ArgumentException>(() => AutoUpdatePlan.CronLine(
			AutoUpdatePlan.Entries(ScopeParts.User, false, "host", "pl", "1000", "/usr/bin/dotnet", "/feed\n* * * * * evil")[0],
			"/s", "/home/pl", "/usr/lib/dotnet", "/usr/bin"));

	[Fact]
	public void Source_LocalPathIsStoredAbsolute_UrlsAndNamesAsGiven()
	{
		var cwd = Path.Combine(Path.GetTempPath(), "work");
		var feed = Path.Combine(cwd, "feed");
		bool Exists(string p) => p == feed;

		Assert.Equal(feed, AutoUpdatePlan.ResolveSource("feed", cwd, Exists));
		Assert.Equal(feed, AutoUpdatePlan.ResolveSource(Path.Combine(".", "feed"), cwd, Exists));
		Assert.Equal("https://feed/a%20b/index.json", AutoUpdatePlan.ResolveSource("https://feed/a%20b/index.json", cwd, Exists));
		Assert.Equal("nuget.org", AutoUpdatePlan.ResolveSource("nuget.org", cwd, Exists));   // a configured source's name
	}
}
