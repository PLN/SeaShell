using System.Collections.Generic;
using Xunit;
using SeaShell.Ipc;
using MessagePack;
using MessagePack.Resolvers;

namespace SeaShell.Engine.Tests;

public class MessageRoundtripTests
{
	private static readonly MessagePackSerializerOptions Opts =
		MessagePackSerializerOptions.Standard
			.WithResolver(ContractlessStandardResolver.Instance);

	[Fact]
	public void ScriptInit_Restart_RoundTrip()
	{
		var init = new ScriptInit(
			"/test.cs", "/dir", new[] { "arg1" },
			null, null, null,
			false, 123, 0, null, false,
			Restart: true, RestartCount: 5);

		var bytes = MessagePackSerializer.Serialize(init, Opts);
		var result = MessagePackSerializer.Deserialize<ScriptInit>(bytes, Opts);

		Assert.True(result.Restart, "Restart field lost during roundtrip");
		Assert.Equal(5, result.RestartCount);
	}

	[Fact]
	public void ScriptInit_MutexScope_RoundTrip()
	{
		var init = new ScriptInit(
			"/test.cs", "/dir", null, null, null, null,
			false, 0, 0, null, false,
			MutexScope: 3, MutexAttach: true);

		var bytes = MessagePackSerializer.Serialize(init, Opts);
		var result = MessagePackSerializer.Deserialize<ScriptInit>(bytes, Opts);

		Assert.Equal(3, result.MutexScope);
		Assert.True(result.MutexAttach);
	}

	[Fact]
	public void ScriptInit_AllNewFields_RoundTrip()
	{
		var init = new ScriptInit(
			"/test.cs", "/dir", new[] { "a" },
			new[] { "/test.cs" },
			new Dictionary<string, string> { ["Serilog"] = "4.0.0" },
			new[] { "/lib/test.dll" },
			true, 999, 3, "state", true,
			Restart: true, RestartCount: 7,
			MutexScope: 2, MutexAttach: true,
			WindowMode: true);

		var bytes = MessagePackSerializer.Serialize(init, Opts);
		var result = MessagePackSerializer.Deserialize<ScriptInit>(bytes, Opts);

		Assert.Equal("/test.cs", result.ScriptPath);
		Assert.True(result.Watch);
		Assert.True(result.Restart);
		Assert.Equal(7, result.RestartCount);
		Assert.Equal(2, result.MutexScope);
		Assert.True(result.MutexAttach);
		Assert.True(result.WindowMode);
	}

	[Fact]
	public void RunResponse_Restart_RoundTrip()
	{
		var resp = new RunResponse(
			true, false, false,
			"/out/test.dll", "/out/test.deps.json", "/out/test.runtimeconfig.json",
			"/out/test.sea.json", 0, null,
			Restart: true, MutexScope: 3);

		var bytes = MessagePackSerializer.Serialize(resp, Opts);
		var result = MessagePackSerializer.Deserialize<RunResponse>(bytes, Opts);

		Assert.True(result.Restart);
		Assert.Equal(3, result.MutexScope);
	}

	[Fact]
	public void ScriptExit_Restart_RoundTrip()
	{
		var exit = new ScriptExit(0, 7, Restart: false);

		var bytes = MessagePackSerializer.Serialize(exit, Opts);
		var result = MessagePackSerializer.Deserialize<ScriptExit>(bytes, Opts);

		Assert.Equal(0, result.ExitCode);
		Assert.Equal(7, result.ExitDelay);
		Assert.False(result.Restart);
	}
}
