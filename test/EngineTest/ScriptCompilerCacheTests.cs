using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SeaShell.Engine;
using Xunit;

namespace SeaShell.Engine.Tests;

public sealed class ScriptCompilerCacheTests : IDisposable
{
	private readonly string _root;

	public ScriptCompilerCacheTests()
	{
		_root = Path.Combine(Path.GetTempPath(), $"seashell-cache-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string WriteScript()
	{
		var script = Path.Combine(_root, "cachetest.cs");
		File.WriteAllText(script, "Console.WriteLine(\"cache test\");\n");
		return script;
	}

	[Fact]
	public void Compile_HalfStagedOutputDir_IsRecompiledNotServed()
	{
		var compiler = new ScriptCompiler(Path.Combine(_root, "cache"));
		var script = WriteScript();

		var first = compiler.Compile(script, Array.Empty<string>());
		Assert.True(first.Success, first.Error);
		var outputDir = Path.GetDirectoryName(first.RuntimeConfigPath)!;

		// Simulate a compile that died after writing the dll + runtimeconfig.
		File.Delete(first.ManifestPath!);
		File.Delete(Path.Combine(outputDir, "cachetest.complete"));

		var second = compiler.Compile(script, Array.Empty<string>());
		Assert.True(second.Success, second.Error);
		Assert.NotNull(second.ManifestPath);
		Assert.True(File.Exists(second.ManifestPath));
		Assert.True(File.Exists(Path.Combine(outputDir, "cachetest.complete")));
	}

	[Fact]
	public void ClearCache_StoppedByLockedFile_LeavesCacheMissNotHit()
	{
		var compiler = new ScriptCompiler(Path.Combine(_root, "cache"));
		var script = WriteScript();

		var first = compiler.Compile(script, Array.Empty<string>());
		Assert.True(first.Success, first.Error);
		var outputDir = Path.GetDirectoryName(first.RuntimeConfigPath)!;
		var marker = Path.Combine(outputDir, "cachetest.complete");

		// A running script holds its dll; on Windows that stops the delete halfway.
		using (new FileStream(Path.Combine(outputDir, "cachetest.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
			compiler.ClearCache(script);

		Assert.False(File.Exists(marker));
		var second = compiler.Compile(script, Array.Empty<string>());
		Assert.True(second.Success, second.Error);
		Assert.True(File.Exists(second.ManifestPath));
		Assert.True(File.Exists(second.DepsJsonPath));
		Assert.True(File.Exists(marker));
	}

	[Fact]
	public async Task Compile_ConcurrentSameScript_EveryResultIsComplete()
	{
		var compiler = new ScriptCompiler(Path.Combine(_root, "cache"));
		var script = WriteScript();

		var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
			Task.Run(() => compiler.Compile(script, Array.Empty<string>()))));

		Assert.All(results, r =>
		{
			Assert.True(r.Success, r.Error);
			Assert.NotNull(r.ManifestPath);
			Assert.True(File.Exists(r.ManifestPath));
		});
	}
}
