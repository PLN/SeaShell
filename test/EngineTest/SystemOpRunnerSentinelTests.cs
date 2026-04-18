using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace EngineTest;

public class SystemOpRunnerSentinelTests : IDisposable
{
	private readonly string _stageDir;
	private readonly MethodInfo _readResult;
	private readonly Type _resultType;

	public SystemOpRunnerSentinelTests()
	{
		_stageDir = Path.Combine(Path.GetTempPath(), $"seashell-sentinel-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_stageDir);

		var runnerType = typeof(SeaShell.Installer.LegacyCleanup).Assembly
			.GetType("SeaShell.Installer.SystemOpRunner")!;
		_readResult = runnerType.GetMethod("ReadResult", BindingFlags.NonPublic | BindingFlags.Static)!;
		_resultType = runnerType.GetNestedType("Result")!;
	}

	public void Dispose()
	{
		try { Directory.Delete(_stageDir, recursive: true); } catch { }
	}

	private (int ExitCode, string Output) Read(string? sentinel, string? output)
	{
		var sentinelPath = Path.Combine(_stageDir, "op.result");
		var outputPath = Path.Combine(_stageDir, "op.output");
		if (sentinel != null) File.WriteAllText(sentinelPath, sentinel);
		if (output != null) File.WriteAllText(outputPath, output);

		var r = _readResult.Invoke(null, new object[] { sentinelPath, outputPath })!;
		var exit = (int)_resultType.GetProperty("ExitCode")!.GetValue(r)!;
		var outStr = (string)_resultType.GetProperty("Output")!.GetValue(r)!;
		return (exit, outStr);
	}

	[Fact]
	public void HappyPath_ExitCodeAndOutputReturned()
	{
		var (exit, output) = Read(
			sentinel: "EXITCODE=0\nstage=install-complete\n",
			output: "hello from system\n");
		Assert.Equal(0, exit);
		Assert.Equal("hello from system\n", output);
	}

	[Fact]
	public void NonZeroExitCode_Parsed()
	{
		var (exit, _) = Read(sentinel: "EXITCODE=42\nstage=failed\n", output: null);
		Assert.Equal(42, exit);
	}

	[Fact]
	public void MissingExitCodeLine_YieldsMinusOneSentinel()
	{
		var (exit, _) = Read(sentinel: "stage=lost\n", output: null);
		Assert.Equal(-1, exit);
	}

	[Fact]
	public void BogusExitCodeLine_YieldsMinusOneSentinel()
	{
		var (exit, _) = Read(sentinel: "EXITCODE=not-a-number\n", output: null);
		Assert.Equal(-1, exit);
	}

	[Fact]
	public void MissingOutputFile_YieldsEmptyString()
	{
		var (_, output) = Read(sentinel: "EXITCODE=0\n", output: null);
		Assert.Equal("", output);
	}
}
