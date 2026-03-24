using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using SeaShell.Engine;

namespace SeaShell.Engine.Tests;

public class DepsJsonWriterTests : IDisposable
{
	private readonly string _tempFile;
	private readonly string _engineDir;

	public DepsJsonWriterTests()
	{
		_tempFile = Path.Combine(Path.GetTempPath(), $"seashell-test-{Guid.NewGuid():N}.deps.json");
		// Use the Engine's own output dir as engineDir (has the bundled DLLs)
		_engineDir = Path.GetDirectoryName(typeof(DepsJsonWriter).Assembly.Location)!;
	}

	public void Dispose()
	{
		if (File.Exists(_tempFile)) File.Delete(_tempFile);
	}

	// ── Bundled DLLs must be present as type "project" ─────────────

	[Theory]
	[InlineData("MessagePack")]
	[InlineData("SeaShell.Ipc")]
	[InlineData("SeaShell.Script")]
	public void Write_BundledLibrary_InTargets(string libraryName)
	{
		DepsJsonWriter.Write(_tempFile, "test", new List<NuGetResolver.ResolvedPackage>(), _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var targets = doc.RootElement.GetProperty("targets");
		var tfm = targets.EnumerateObject().First().Value;

		Assert.Contains(tfm.EnumerateObject(), e => e.Name.StartsWith(libraryName + "/"));
	}

	[Theory]
	[InlineData("MessagePack")]
	[InlineData("SeaShell.Ipc")]
	[InlineData("SeaShell.Script")]
	public void Write_BundledLibrary_InLibraries_AsProject(string libraryName)
	{
		DepsJsonWriter.Write(_tempFile, "test", new List<NuGetResolver.ResolvedPackage>(), _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var libraries = doc.RootElement.GetProperty("libraries");

		var entry = libraries.EnumerateObject()
			.FirstOrDefault(e => e.Name.StartsWith(libraryName + "/"));
		Assert.NotEqual(default, entry);
		Assert.Equal("project", entry.Value.GetProperty("type").GetString());
	}

	[Theory]
	[InlineData("MessagePack")]
	[InlineData("SeaShell.Ipc")]
	[InlineData("SeaShell.Script")]
	public void Write_BundledLibrary_VersionNotHardcoded(string libraryName)
	{
		DepsJsonWriter.Write(_tempFile, "test", new List<NuGetResolver.ResolvedPackage>(), _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var libraries = doc.RootElement.GetProperty("libraries");

		// Version must not be 1.0.0 (the old hardcoded value).
		// In test environments where bundled DLLs aren't present, GetBundledVersion
		// returns "0.0.0" as a safe fallback — that's acceptable.
		var entry = libraries.EnumerateObject()
			.First(e => e.Name.StartsWith(libraryName + "/"));
		var version = entry.Name.Split('/')[1];
		Assert.NotEqual("1.0.0", version);
	}

	// ── Structure ──────────────────────────────────────────────────

	[Fact]
	public void Write_EmptyPackages_ProducesValidJson()
	{
		DepsJsonWriter.Write(_tempFile, "test", new List<NuGetResolver.ResolvedPackage>(), _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		Assert.True(doc.RootElement.TryGetProperty("runtimeTarget", out _));
		Assert.True(doc.RootElement.TryGetProperty("targets", out _));
		Assert.True(doc.RootElement.TryGetProperty("libraries", out _));
	}
}
