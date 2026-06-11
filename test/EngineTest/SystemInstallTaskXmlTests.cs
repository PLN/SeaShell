using System;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace EngineTest;

public class SystemInstallTaskXmlTests
{
	// DaemonTasks.BuildTaskXml + enums are internal; reach via reflection
	// (InternalsVisibleTo on Installer would also work, but tests project is
	// already referencing Installer publicly — reflection keeps the surface tidy).
	private static readonly Type DaemonTasks =
		typeof(SeaShell.Installer.LegacyCleanup).Assembly.GetType("SeaShell.Installer.DaemonTasks")!;
	private static readonly Type TaskPrincipal = DaemonTasks.GetNestedType("TaskPrincipal", BindingFlags.NonPublic)!;
	private static readonly Type TaskTrigger = DaemonTasks.GetNestedType("TaskTrigger", BindingFlags.NonPublic)!;
	private static readonly MethodInfo BuildTaskXml = DaemonTasks.GetMethod(
		"BuildTaskXml", BindingFlags.NonPublic | BindingFlags.Static)!;

	private static string Build(string principal, string trigger, string runLevel, string desc = "test", string? userSid = null)
	{
		var p = Enum.Parse(TaskPrincipal, principal);
		var t = Enum.Parse(TaskTrigger, trigger);
		return (string)BuildTaskXml.Invoke(null, new object?[] { "cmd.exe", null, p, t, runLevel, desc, userSid })!;
	}

	private static XDocument Parse(string xml) => XDocument.Parse(xml);

	private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

	[Fact]
	public void System_Principal_UsesWellKnownSidWithoutLogonType()
	{
		var doc = Parse(Build("System", "Boot", "HighestAvailable"));
		var principal = doc.Descendants(Ns + "Principal").Single();

		Assert.Equal("S-1-5-18", principal.Element(Ns + "UserId")!.Value);
		// Task Scheduler rejects an explicit <LogonType>ServiceAccount</LogonType> —
		// infer it from the SID instead, so the element must be absent.
		Assert.Null(principal.Element(Ns + "LogonType"));
		Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
	}

	[Fact]
	public void System_Trigger_IsBootTriggerNotLogonTrigger()
	{
		var doc = Parse(Build("System", "Boot", "HighestAvailable"));
		var triggers = doc.Descendants(Ns + "Triggers").Single();

		Assert.NotNull(triggers.Element(Ns + "BootTrigger"));
		Assert.Null(triggers.Element(Ns + "LogonTrigger"));
	}

	[Fact]
	public void None_Trigger_YieldsEmptyTriggersBlock()
	{
		var doc = Parse(Build("System", "None", "HighestAvailable"));
		var triggers = doc.Descendants(Ns + "Triggers").Single();

		Assert.Empty(triggers.Elements());
	}

	[Fact]
	public void CurrentUser_Principal_UsesInteractiveTokenAndExplicitSid()
	{
		var doc = Parse(Build("CurrentUser", "Logon", "LeastPrivilege",
			userSid: "S-1-5-21-1111111111-2222222222-3333333333-1001"));
		var principal = doc.Descendants(Ns + "Principal").Single();

		Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
		Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-1001",
			principal.Element(Ns + "UserId")!.Value);
	}

	[Fact]
	public void CurrentUser_LogonTrigger_BindsSameSidAsPrincipal()
	{
		var doc = Parse(Build("CurrentUser", "Logon", "LeastPrivilege",
			userSid: "S-1-5-21-1111111111-2222222222-3333333333-1001"));
		var logon = doc.Descendants(Ns + "LogonTrigger").Single();

		Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-1001",
			logon.Element(Ns + "UserId")!.Value);
	}

	[Fact]
	public void CurrentUser_WithoutExplicitSid_FallsBackToAmbientToken()
	{
		var doc = Parse(Build("CurrentUser", "Logon", "LeastPrivilege"));
		var principal = doc.Descendants(Ns + "Principal").Single();

		Assert.Equal(SeaShell.SeaShellIdentity.Current.Token.ToUpperInvariant(),
			principal.Element(Ns + "UserId")!.Value);
	}

	[Fact]
	public void Description_LandsInRegistrationInfo()
	{
		var doc = Parse(Build("System", "Boot", "HighestAvailable", desc: "UniqueProbeDescription"));
		Assert.Equal("UniqueProbeDescription",
			doc.Descendants(Ns + "RegistrationInfo").Single().Element(Ns + "Description")!.Value);
	}
}
