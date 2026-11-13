using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class ScopeResolverTests
{
	private static readonly ScopeFileRead NoFile = new(ScopeFileState.Missing, ScopeParts.None);
	private static ScopeFileRead File(ScopeParts parts) => new(ScopeFileState.Read, parts);

	private static Func<ScopeDetection> Detected(PartPresence elevator, PartPresence system) =>
		() => new ScopeDetection(elevator, system);

	private static readonly Func<ScopeDetection> MustNotDetect =
		() => throw new InvalidOperationException("detection must not run");

	private static readonly ScopeRequest NoFlags = new();

	[Fact]
	public void RecordedScope_IsUsedWithoutProbingTheMachine()
	{
		var r = ScopeResolver.Resolve(NoFlags, File(ScopeParts.User | ScopeParts.System), MustNotDetect, isWindows: true);

		Assert.Equal(ScopeSource.Recorded, r.Source);
		Assert.Equal(ScopeParts.User | ScopeParts.System, r.Parts);
		Assert.True(r.Record);
	}

	[Fact]
	public void NoFile_CertainDetection_InstallsAndRecordsWhatExists()
	{
		// A 0.6.2 `install --all`: elevator and SYSTEM found.
		var r = ScopeResolver.Resolve(NoFlags, NoFile, Detected(PartPresence.Present, PartPresence.Present), isWindows: true);

		Assert.Equal(ScopeParts.User | ScopeParts.Elevator | ScopeParts.System, r.Parts);
		Assert.Equal(ScopeParts.None, r.Unresolved);
		Assert.True(r.Record);
	}

	[Fact]
	public void NoFile_SystemAbsent_IsACertainUserOnlyScope()
	{
		var r = ScopeResolver.Resolve(NoFlags, NoFile, Detected(PartPresence.Absent, PartPresence.Absent), isWindows: true);

		Assert.Equal(ScopeParts.User, r.Parts);
		Assert.Equal(ScopeParts.None, r.Unresolved);
		Assert.True(r.Record);
	}

	[Fact]
	public void NoFile_SystemInaccessible_UserOnlyButNothingRecorded()
	{
		// A denied query must never produce a narrower recorded scope.
		var r = ScopeResolver.Resolve(NoFlags, NoFile, Detected(PartPresence.Absent, PartPresence.Unknown), isWindows: true);

		Assert.Equal(ScopeParts.User, r.Parts);
		Assert.Equal(ScopeParts.System, r.Unresolved);
		Assert.False(r.Record);
	}

	[Fact]
	public void ElevatedHelperResolvesTheUncertainty_ThenTheScopeIsRecorded()
	{
		var r = ScopeResolver.Resolve(NoFlags, NoFile, Detected(PartPresence.Present, PartPresence.Unknown), isWindows: true);

		var found = ScopeResolver.WithResolved(r, new ScopeDetection(PartPresence.Present, PartPresence.Present));
		Assert.Equal(ScopeParts.User | ScopeParts.Elevator | ScopeParts.System, found.Parts);
		Assert.True(found.Record);

		var absent = ScopeResolver.WithResolved(r, new ScopeDetection(PartPresence.Present, PartPresence.Absent));
		Assert.Equal(ScopeParts.User | ScopeParts.Elevator, absent.Parts);
		Assert.True(absent.Record);

		var stillUnknown = ScopeResolver.WithResolved(r, new ScopeDetection(PartPresence.Present, PartPresence.Unknown));
		Assert.False(stillUnknown.Record);
	}

	[Fact]
	public void UnreadableFile_FallsBackToDetectionAndIsNeverOverwritten()
	{
		var unreadable = new ScopeFileRead(ScopeFileState.Unreadable, ScopeParts.None, "Access denied");

		var r = ScopeResolver.Resolve(NoFlags, unreadable, Detected(PartPresence.Present, PartPresence.Unknown), isWindows: true);
		Assert.False(r.Record);

		var resolved = ScopeResolver.WithResolved(r, new ScopeDetection(PartPresence.Present, PartPresence.Absent));
		Assert.False(resolved.Record);
	}

	[Theory]
	[InlineData(true, ScopeParts.User | ScopeParts.Elevator | ScopeParts.System)]
	[InlineData(false, ScopeParts.User | ScopeParts.System)]   // no elevator on Linux
	public void AllFlag_IsEverythingThePlatformSupportsAndReRecords(bool isWindows, ScopeParts expected)
	{
		var r = ScopeResolver.Resolve(new ScopeRequest { All = true }, File(ScopeParts.User), MustNotDetect, isWindows);

		Assert.Equal(expected, r.Parts);
		Assert.True(r.Record);
	}

	[Fact]
	public void UserFlag_OverAWiderScope_ReportsWhatIsLeftBehind()
	{
		var r = ScopeResolver.Resolve(new ScopeRequest { User = true }, File(ScopeParts.User | ScopeParts.Elevator | ScopeParts.System), MustNotDetect, isWindows: true);

		Assert.Equal(ScopeParts.User, r.Parts);
		Assert.Equal(ScopeParts.Elevator | ScopeParts.System, r.LeftBehind);
		Assert.True(r.Record);
	}

	[Fact]
	public void UserFlag_WithoutAFile_LeftBehindComesFromDetection()
	{
		var r = ScopeResolver.Resolve(new ScopeRequest { User = true }, NoFile, Detected(PartPresence.Absent, PartPresence.Present), isWindows: true);

		Assert.Equal(ScopeParts.System, r.LeftBehind);
	}

	[Fact]
	public void SystemFlagAndInternalPass_NeverRewriteTheUsersScope()
	{
		var system = ScopeResolver.Resolve(new ScopeRequest { System = true }, File(ScopeParts.User), MustNotDetect, isWindows: true);
		Assert.Equal(ScopeParts.System, system.Parts);
		Assert.False(system.Record);

		var pass = ScopeResolver.Resolve(new ScopeRequest { Pass = ScopeParts.User | ScopeParts.Elevator }, File(ScopeParts.User), MustNotDetect, isWindows: true);
		Assert.Equal(ScopeParts.User | ScopeParts.Elevator, pass.Parts);
		Assert.False(pass.Record);
	}

	[Fact]
	public void ConflictingScopeFlags_AreRejected()
	{
		Assert.NotNull(ScopeResolver.Validate(new ScopeRequest { All = true, User = true }));
		Assert.NotNull(ScopeResolver.Validate(new ScopeRequest { System = true, Pass = ScopeParts.User }));
		Assert.Null(ScopeResolver.Validate(new ScopeRequest { All = true }));
	}

	[Fact]
	public void ScopeFile_IgnoresUnknownTokens_ButPassListIsStrict()
	{
		// install.scope is shared between bootstrapper versions; --pass is internal and a typo must fail.
		Assert.Equal(ScopeParts.User | ScopeParts.System, ScopeTokens.ParseFile("user\nauto-update\nsystem\n"));
		Assert.False(ScopeTokens.TryParseList("user,sytem", out _));
		Assert.True(ScopeTokens.TryParseList("user,elevator", out var parts));
		Assert.Equal(ScopeParts.User | ScopeParts.Elevator, parts);
	}
}
