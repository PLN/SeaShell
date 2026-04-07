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

	// ── Version skew: NuGet provides same-name package with different version ──

	[Theory]
	[InlineData("MessagePack")]
	[InlineData("SeaShell.Ipc")]
	[InlineData("SeaShell.Script")]
	public void Write_NuGetProvidesBundled_BothEntriesAsProject(string packageName)
	{
		// When NuGet provides a package with the same name as a bundled DLL
		// (different version), BOTH entries exist — but both are type:"project"
		// now (all DLLs copied to output dir, no NuGet cache probing).
		var packages = new List<NuGetResolver.ResolvedPackage>
		{
			new()
			{
				Name = packageName,
				Version = "99.0.0",
				PackagePath = $"{packageName.ToLowerInvariant()}/99.0.0",
				CompileDlls = new List<string>(),
				RuntimeAssets = new List<NuGetResolver.ResolvedAsset>
				{
					new() { FullPath = $"/fake/{packageName}.dll", RelativePath = $"lib/net10.0/{packageName}.dll" }
				},
				NativeAssets = new List<NuGetResolver.ResolvedAsset>(),
			}
		};

		DepsJsonWriter.Write(_tempFile, "test", packages, _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var libraries = doc.RootElement.GetProperty("libraries");

		var entries = libraries.EnumerateObject()
			.Where(e => e.Name.StartsWith(packageName + "/"))
			.ToList();

		// Both entries exist (different versions) — both type:"project"
		Assert.Equal(2, entries.Count);
		Assert.All(entries, e => Assert.Equal("project", e.Value.GetProperty("type").GetString()));
	}

	[Fact]
	public void Write_NuGetSameNameSameVersion_SingleEntry()
	{
		// When NuGet version exactly matches the bundled AssemblyVersion,
		// the dictionary key collides and NuGet overwrites the project entry.
		// This is the "lucky" case — but it's fragile.
		var bundledVersion = GetBundledMessagePackVersion();

		var packages = new List<NuGetResolver.ResolvedPackage>
		{
			new()
			{
				Name = "MessagePack",
				Version = bundledVersion,
				PackagePath = $"messagepack/{bundledVersion}",
				CompileDlls = new List<string>(),
				RuntimeAssets = new List<NuGetResolver.ResolvedAsset>
				{
					new() { FullPath = "/fake/MessagePack.dll", RelativePath = "lib/net10.0/MessagePack.dll" }
				},
				NativeAssets = new List<NuGetResolver.ResolvedAsset>(),
			}
		};

		DepsJsonWriter.Write(_tempFile, "test", packages, _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var libraries = doc.RootElement.GetProperty("libraries");

		var entries = libraries.EnumerateObject()
			.Where(e => e.Name.StartsWith("MessagePack/"))
			.ToList();

		// Same version → dict key collision → single entry (NuGet overwrites: type:"project")
		Assert.Single(entries);
		Assert.Equal("project", entries[0].Value.GetProperty("type").GetString());
	}

	// ── engineDir without bundled DLLs ────────────────────────────

	[Theory]
	[InlineData("MessagePack")]
	[InlineData("SeaShell.Ipc")]
	[InlineData("SeaShell.Script")]
	public void Write_EngineDirMissingBundledDlls_FallbackVersions(string libraryName)
	{
		// When engineDir doesn't contain bundled DLLs (e.g., dotnet run with
		// NuGet cache layout), GetBundledVersion falls back to "0.0.0".
		var emptyDir = Path.Combine(Path.GetTempPath(), $"seashell-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(emptyDir);

		try
		{
			DepsJsonWriter.Write(_tempFile, "test", new List<NuGetResolver.ResolvedPackage>(), emptyDir);

			var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
			var libraries = doc.RootElement.GetProperty("libraries");

			var entry = libraries.EnumerateObject()
				.First(e => e.Name.StartsWith(libraryName + "/"));
			var version = entry.Name.Split('/')[1];

			// BUG: version is "0.0.0" because DLLs aren't in engineDir.
			// The deps.json has broken entries that reference nonexistent DLLs.
			Assert.Equal("0.0.0", version);
		}
		finally
		{
			Directory.Delete(emptyDir, true);
		}
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

	// ── Helpers ────────────────────────────────────────────────────

	private string GetBundledMessagePackVersion()
	{
		var path = Path.Combine(_engineDir, "MessagePack.dll");
		if (!File.Exists(path)) return "0.0.0";
		return System.Reflection.AssemblyName.GetAssemblyName(path).Version?.ToString(3) ?? "0.0.0";
	}
}
