using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using SeaShell.Engine;

namespace SeaShell.Engine.Tests;

public class ArtifactWriterTests : IDisposable
{
	private readonly string _tempFile;
	private readonly string _engineDir;

	public ArtifactWriterTests()
	{
		_tempFile = Path.Combine(Path.GetTempPath(), $"seashell-test-{Guid.NewGuid():N}.runtimeconfig.json");
		_engineDir = Path.Combine(Path.GetTempPath(), "seashell-test-engine");
	}

	public void Dispose()
	{
		if (File.Exists(_tempFile)) File.Delete(_tempFile);
	}

	// ── Probing paths ──────────────────────────────────────────────

	[Fact]
	public void WriteRuntimeConfig_IncludesEngineDir()
	{
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: false, _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var paths = doc.RootElement
			.GetProperty("runtimeOptions")
			.GetProperty("additionalProbingPaths");

		var pathList = paths.EnumerateArray().Select(e => e.GetString()).ToList();
		Assert.Contains(_engineDir, pathList);
	}

	[Fact]
	public void WriteRuntimeConfig_IncludesNuGetCache()
	{
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: false, _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var paths = doc.RootElement
			.GetProperty("runtimeOptions")
			.GetProperty("additionalProbingPaths");

		var pathList = paths.EnumerateArray().Select(e => e.GetString()).ToList();
		Assert.Contains(pathList, p => p != null && p.Contains(".nuget"));
	}

	// ── Framework references ───────────────────────────────────────

	[Fact]
	public void WriteRuntimeConfig_Default_OnlyNetCoreFramework()
	{
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: false, _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var frameworks = doc.RootElement
			.GetProperty("runtimeOptions")
			.GetProperty("frameworks");

		Assert.Equal(1, frameworks.GetArrayLength());
		Assert.Equal("Microsoft.NETCore.App", frameworks[0].GetProperty("name").GetString());
	}

	[Fact]
	public void WriteRuntimeConfig_WebApp_IncludesAspNetFramework()
	{
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: true, _engineDir);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var frameworks = doc.RootElement
			.GetProperty("runtimeOptions")
			.GetProperty("frameworks");

		Assert.Equal(2, frameworks.GetArrayLength());
		var names = Enumerable.Range(0, 2)
			.Select(i => frameworks[i].GetProperty("name").GetString())
			.ToList();
		Assert.Contains("Microsoft.NETCore.App", names);
		Assert.Contains("Microsoft.AspNetCore.App", names);
	}
}
