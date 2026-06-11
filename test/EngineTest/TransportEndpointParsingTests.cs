using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SeaShell;
using SeaShell.Protocol;
using Xunit;

namespace EngineTest;

public class TransportEndpointParsingTests
{
	// TryParseAnyEntry is private; reach via reflection (same pattern as
	// SystemInstallTaskXmlTests).
	private static readonly MethodInfo TryParse = typeof(TransportEndpoint).GetMethod(
		"TryParseAnyEntry", BindingFlags.NonPublic | BindingFlags.Static)!;

	private static (string Version, string Identity, string Address, bool IsElevator)? Parse(string baseName)
	{
		// Build the platform-shaped raw entry the enumerator would hand over.
		var rawEntry = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? @"\\.\pipe\" + baseName
			: "/tmp/" + baseName + ".sock";

		var result = TryParse.Invoke(null, new object?[] { rawEntry });
		if (result == null) return null;

		var t = result.GetType();
		return (
			(string)t.GetField("Item1")!.GetValue(result)!,
			(string)t.GetField("Item2")!.GetValue(result)!,
			(string)t.GetField("Item3")!.GetValue(result)!,
			(bool)t.GetField("Item4")!.GetValue(result)!);
	}

	[Fact]
	public void Legacy_UsernameToken_ParsesAsBefore()
	{
		var parsed = Parse("seashell-0.5.1.240-qd");
		Assert.NotNull(parsed);
		Assert.Equal("0.5.1.240", parsed!.Value.Version);
		Assert.Equal("qd", parsed.Value.Identity);
		Assert.False(parsed.Value.IsElevator);
	}

	[Fact]
	public void Legacy_MachineDollarToken_Parses()
	{
		// Pre-0.6.0 SYSTEM daemons: Environment.UserName under LocalSystem.
		var parsed = Parse("seashell-0.5.1.240-myhost$");
		Assert.NotNull(parsed);
		Assert.Equal("myhost$", parsed!.Value.Identity);
	}

	[Fact]
	public void SidToken_WithHyphens_Parses()
	{
		var parsed = Parse("seashell-0.6.0.241-s-1-5-18");
		Assert.NotNull(parsed);
		Assert.Equal("0.6.0.241", parsed!.Value.Version);
		Assert.Equal("s-1-5-18", parsed.Value.Identity);
		Assert.False(parsed.Value.IsElevator);
	}

	[Fact]
	public void UserSidToken_LongForm_Parses()
	{
		var parsed = Parse("seashell-0.6.0.241-s-1-5-21-1111111111-2222222222-3333333333-1001");
		Assert.NotNull(parsed);
		Assert.Equal("s-1-5-21-1111111111-2222222222-3333333333-1001", parsed!.Value.Identity);
	}

	[Fact]
	public void Elevator_SidToken_ParsesAsElevator()
	{
		var parsed = Parse("seashell-elevated-0.6.0.241-s-1-5-21-1111111111-2222222222-3333333333-1001");
		Assert.NotNull(parsed);
		Assert.True(parsed!.Value.IsElevator);
		Assert.Equal("0.6.0.241", parsed.Value.Version);
	}

	[Fact]
	public void GuidScriptPipes_AreRejected()
	{
		// Script pipes: SEASHELL_PIPE=seashell-{guid}. Both GUID formats must
		// be filtered — neither has a parseable dotted version segment.
		Assert.Null(Parse("seashell-c5f2a1d44e0b4f6a9d3c8b7a6e5d4c3b"));
		Assert.Null(Parse("seashell-c5f2a1d4-4e0b-4f6a-9d3c-8b7a6e5d4c3b"));
	}

	[Fact]
	public void AttachPipes_AreRejected()
	{
		Assert.Null(Parse("seashell-attach-1a2b3c4d"));
	}

	[Fact]
	public void NonSeashellNames_AreRejected()
	{
		Assert.Null(Parse("mysql-socket"));
		Assert.Null(Parse("seashell-"));
	}
}

public class SeaShellIdentityTests
{
	[Fact]
	public void Current_TokenIsLowercaseAndMatchesItself()
	{
		var id = SeaShellIdentity.Current;
		Assert.Equal(id.Token.ToLowerInvariant(), id.Token);
		Assert.True(id.Matches(id.Token));
		Assert.True(id.Matches(id.LegacyToken));
		Assert.False(id.Matches("someone-else"));
	}

	[Fact]
	public void Current_WindowsTokenIsSid_UnixTokenIsUid()
	{
		var id = SeaShellIdentity.Current;
		if (OperatingSystem.IsWindows())
			Assert.StartsWith("s-1-", id.Token);
		else
			Assert.True(uint.TryParse(id.Token, out _));
	}

	[Fact]
	public void Current_LegacyTokenIsLowercaseUserName()
	{
		Assert.Equal(Environment.UserName.ToLowerInvariant(), SeaShellIdentity.Current.LegacyToken);
	}

	[Fact]
	public void LocalSystem_IsWellKnownAndComputableFromUserContext()
	{
		if (!OperatingSystem.IsWindows()) return;

		var sys = SeaShellIdentity.LocalSystem;
		Assert.Equal("s-1-5-18", sys.Token);
		Assert.True(sys.IsSystem);
		Assert.Equal("SYSTEM", sys.DisplayName);
		Assert.EndsWith(
			@"systemprofile\AppData\Local\seashell",
			sys.DataDir,
			StringComparison.OrdinalIgnoreCase);
		Assert.Equal($"{Environment.MachineName.ToLowerInvariant()}$", sys.LegacyToken);
	}

	[Fact]
	public void TransportIdentity_UsesCanonicalToken()
	{
		Assert.Equal(SeaShellIdentity.Current.Token, TransportEndpoint.CurrentUserIdentity);
	}
}
