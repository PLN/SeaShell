using System.Xml.Linq;
using Xunit;
using SeaShell.Installer;

namespace SeaShell.Engine.Tests;

public class TaskPathRefresherTests
{
	// Sample XML modelled on ScriptScheduler.BuildScriptTaskXml — same element
	// tree, same namespace. A stale pre-0.5.0 Exec/Command path should be
	// rewritten; everything else (triggers, principal, args, working dir)
	// must pass through untouched.
	private const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
	private const string LegacyCommand = @"C:\Users\qd\AppData\Local\seashell\bin\seaw.exe";
	private const string CurrentCommand = @"C:\Users\qd\AppData\Local\seashell\current\bin\seaw.exe";

	private static string BuildXml(string command) => $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""{TaskNs}"">
  <RegistrationInfo><Description>seashell scheduled: backup</Description></RegistrationInfo>
  <Triggers><CalendarTrigger><StartBoundary>2026-01-01T10:00:00</StartBoundary><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>DOM\qd</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
  <Actions>
    <Exec>
      <Command>{command}</Command>
      <Arguments>C:\scripts\backup.cs</Arguments>
      <WorkingDirectory>C:\scripts</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

	[Fact]
	public void RewriteXmlCommand_replaces_exec_command()
	{
		var input = BuildXml(LegacyCommand);

		var output = TaskPathRefresher.RewriteXmlCommand(input, CurrentCommand);

		Assert.NotNull(output);
		var doc = XDocument.Parse(output!);
		XNamespace ns = TaskNs;
		var cmd = doc.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value;
		Assert.Equal(CurrentCommand, cmd);

		// Everything else preserved
		var args = doc.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Arguments")!.Value;
		Assert.Equal(@"C:\scripts\backup.cs", args);
		var wd = doc.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "WorkingDirectory")!.Value;
		Assert.Equal(@"C:\scripts", wd);
		var user = doc.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!.Element(ns + "UserId")!.Value;
		Assert.Equal(@"DOM\qd", user);
	}

	[Fact]
	public void RewriteXmlCommand_noop_when_already_current()
	{
		var input = BuildXml(CurrentCommand);

		var output = TaskPathRefresher.RewriteXmlCommand(input, CurrentCommand);

		Assert.Null(output);
	}

	[Fact]
	public void RewriteXmlCommand_returns_null_when_xml_has_no_exec()
	{
		const string noExec = @"<?xml version=""1.0""?><Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""/>";

		var output = TaskPathRefresher.RewriteXmlCommand(noExec, CurrentCommand);

		Assert.Null(output);
	}

	[Fact]
	public void RewriteCronLine_replaces_sea_path_unquoted()
	{
		var input = "0 10 * * * cd /home/qd/scripts && /home/qd/.local/share/seashell/bin/sea /home/qd/scripts/backup.cs # seashell:backup";

		var output = TaskPathRefresher.RewriteCronLine(input, "/home/qd/.local/share/seashell/current/bin/sea");

		Assert.NotNull(output);
		Assert.Equal(
			"0 10 * * * cd /home/qd/scripts && /home/qd/.local/share/seashell/current/bin/sea /home/qd/scripts/backup.cs # seashell:backup",
			output);
	}

	[Fact]
	public void RewriteCronLine_replaces_sea_path_quoted_with_spaces()
	{
		var input = "@reboot cd '/home/qd/my scripts' && '/home/qd/old path/sea' '/home/qd/my scripts/boot.cs' # seashell:boot";

		var output = TaskPathRefresher.RewriteCronLine(input, "/home/qd/.local/share/seashell/current/bin/sea");

		Assert.NotNull(output);
		Assert.Contains("&& /home/qd/.local/share/seashell/current/bin/sea ", output);
		Assert.Contains("# seashell:boot", output);
	}

	[Fact]
	public void RewriteCronLine_noop_when_already_current()
	{
		var target = "/home/qd/.local/share/seashell/current/bin/sea";
		var input = $"0 10 * * * cd /scripts && {target} /scripts/backup.cs # seashell:backup";

		var output = TaskPathRefresher.RewriteCronLine(input, target);

		Assert.Null(output);
	}

	[Fact]
	public void RewriteCronLine_returns_null_when_not_tagged()
	{
		var input = "0 10 * * * /usr/bin/something-else /path/script.sh";

		var output = TaskPathRefresher.RewriteCronLine(input, "/any/sea");

		Assert.Null(output);
	}
}
