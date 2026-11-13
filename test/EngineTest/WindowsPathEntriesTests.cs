using System;
using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class WindowsPathEntriesTests
{
	[Fact]
	public void Strip_RemovesMatchingEntriesAndKeepsTheRestInOrder()
	{
		var result = WindowsPathEntries.Strip(
			@"C:\a;C:\Stale;C:\b;C:\Stale",
			new[] { @"c:\stale" });

		Assert.Equal(@"C:\a;C:\b", result.Path);
		Assert.True(result.Changed);
	}

	[Fact]
	public void Strip_NoMatch_ReportsUnchangedSoNothingIsWritten()
	{
		const string path = @"C:\a;C:\b";

		var result = WindowsPathEntries.Strip(path, new[] { @"C:\Stale" });

		Assert.Equal(path, result.Path);
		Assert.False(result.Changed);
	}

	[Fact]
	public void Strip_SimilarButDifferentEntry_IsKept()
	{
		// A prefix match is not the same directory.
		var result = WindowsPathEntries.Strip(
			@"C:\seashell\bin2;C:\seashell\bin",
			new[] { @"C:\seashell\bin" });

		Assert.Equal(@"C:\seashell\bin2", result.Path);
	}

	[Fact]
	public void Strip_EmptyAndNullPaths_AreNotChanged()
	{
		Assert.False(WindowsPathEntries.Strip("", new[] { @"C:\a" }).Changed);
		Assert.False(WindowsPathEntries.Strip(null, new[] { @"C:\a" }).Changed);
	}

	[Fact]
	public void StripWhere_AppliesThePredicateToEveryEntry()
	{
		var result = WindowsPathEntries.StripWhere(
			@"C:\ProgramData\seashell;C:\Windows;C:\ProgramData\seashell\bin",
			entry => entry.StartsWith(@"C:\ProgramData\seashell", StringComparison.OrdinalIgnoreCase));

		Assert.Equal("C:\\Windows", result.Path);
		Assert.True(result.Changed);
	}

	private const string Bin = @"C:\Users\u\AppData\Local\seashell\current\bin";
	private const string Tools = @"C:\Users\u\.dotnet\tools";

	[Fact]
	public void EnsureBefore_Missing_IsPrepended()
	{
		var result = WindowsPathEntries.EnsureBefore($@"C:\a;{Tools}", Bin, Tools);

		Assert.Equal($@"{Bin};C:\a;{Tools}", result.Path);
		Assert.True(result.Changed);
	}

	[Fact]
	public void EnsureBefore_BehindTheToolsDir_MovesJustAheadOfIt()
	{
		// An older install appended after the SDK's tools entry: the global
		// tool's `seashell` would win. Every other entry keeps its place.
		var result = WindowsPathEntries.EnsureBefore($@"C:\a;{Tools};C:\b;{Bin}\;C:\c", Bin, Tools);

		Assert.Equal($@"C:\a;{Bin}\;{Tools};C:\b;C:\c", result.Path);
		Assert.True(result.Changed);
	}

	[Theory]
	[InlineData(@"C:\a;C:\Users\u\AppData\Local\seashell\current\bin;C:\Users\u\.dotnet\tools")]
	[InlineData(@"C:\a;C:\USERS\U\APPDATA\LOCAL\SEASHELL\CURRENT\BIN")]   // no tools entry; case-insensitive match
	public void EnsureBefore_AlreadyAhead_IsUnchanged(string path)
	{
		var result = WindowsPathEntries.EnsureBefore(path, Bin, Tools);

		Assert.Equal(path, result.Path);
		Assert.False(result.Changed);
	}

	// The raw registry value keeps %VAR% entries; only comparisons expand them.
	private static string Expand(string entry) => entry.Replace("%USERPROFILE%", @"C:\Users\u");

	[Fact]
	public void RawPath_LiteralVariableEntriesSurviveAMove()
	{
		// E2E defect 3: every write expanded %USERPROFILE% entries for good.
		const string raw = @"%USERPROFILE%\.dotnet\tools;%USERPROFILE%\go\bin;C:\Users\u\AppData\Local\seashell\current\bin";

		var result = WindowsPathEntries.EnsureBefore(raw, Bin, Tools, Expand);

		Assert.Equal(@"C:\Users\u\AppData\Local\seashell\current\bin;%USERPROFILE%\.dotnet\tools;%USERPROFILE%\go\bin", result.Path);
		Assert.True(result.Changed);
	}

	[Fact]
	public void RawPath_RemoveMatchesAnExpandedEntryAndKeepsTheOthersLiteral()
	{
		const string raw = @"%USERPROFILE%\go\bin;%USERPROFILE%\AppData\Local\seashell\current\bin;%PATH_EXTRA%";

		var result = WindowsPathEntries.Strip(raw, new[] { Bin }, Expand);

		Assert.Equal(@"%USERPROFILE%\go\bin;%PATH_EXTRA%", result.Path);
		Assert.True(result.Changed);
	}
}
