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
}