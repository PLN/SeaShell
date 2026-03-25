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

	public ArtifactWriterTests()
	{
		_tempFile = Path.Combine(Path.GetTempPath(), $"seashell-test-{Guid.NewGuid():N}.runtimeconfig.json");
	}

	public void Dispose()
	{
		if (File.Exists(_tempFile)) File.Delete(_tempFile);
	}

	// ── Self-contained output (no probing paths) ──────────────────

	[Fact]
	public void WriteRuntimeConfig_NoAdditionalProbingPaths()
	{
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: false);

		var doc = JsonDocument.Parse(File.ReadAllText(_tempFile));
		var rtOpts = doc.RootElement.GetProperty("runtimeOptions");

		// Output is self-contained — all DLLs are in the app base dir.
		// No additionalProbingPaths should be present.
		Assert.False(rtOpts.TryGetProperty("additionalProbingPaths", out _));
	}

	// ── Framework references ───────────────────────────────────────

	[Fact]
	public void WriteRuntimeConfig_Default_OnlyNetCoreFramework()
	{
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: false);

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
		ArtifactWriter.WriteRuntimeConfig(_tempFile, webApp: true);

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
