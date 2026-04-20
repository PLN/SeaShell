using System;
using System.IO;
using System.Text;
using Xunit;
using SeaShell;
using SeaShell.Ipc;

namespace SeaShell.Engine.Tests;

// Invariant under test: after SeaConsole.EnableRelay, writes to System.Console.Out
// and System.Console.Error flow through the IPC channel. Without this, pre-compiled
// libraries like Serilog's Console sink bypass the relay (their IL resolves
// System.Console directly, not the source-level `global using Console = SeaConsole`
// alias).
//
// We cannot ReferenceEquals the writers — .NET wraps the argument of Console.SetOut
// in a SyncTextWriter for thread safety — so we observe byte flow instead: write a
// unique marker via System.Console.Out and assert the bytes appear on the wire.
//
// This test hijacks the process-wide System.Console streams; the finally block
// restores them but SeaConsole's internal _channel remains set (no DisableRelay
// method by design — relay is one-shot per script process). Do not add more
// SeaConsole tests here without accounting for that sticky state.
public class SeaConsoleRelayTests
{
	[Fact]
	public void EnableRelay_Routes_SystemConsoleWrites_Through_Channel()
	{
		var originalOut = Console.Out;
		var originalError = Console.Error;
		var originalIn = Console.In;
		try
		{
			using var ms = new MemoryStream();
			var channel = new MessageChannel(ms, leaveOpen: true);

			SeaConsole.EnableRelay(channel);
			Assert.True(SeaConsole.IsRelayEnabled);

			const string stdoutMarker = "sea-relay-stdout-7f3a";
			const string stderrMarker = "sea-relay-stderr-7f3a";
			Console.Out.Write(stdoutMarker);
			Console.Out.Flush();
			Console.Error.Write(stderrMarker);
			Console.Error.Flush();

			var wire = ms.ToArray();
			Assert.Contains(stdoutMarker, Encoding.UTF8.GetString(wire));
			Assert.Contains(stderrMarker, Encoding.UTF8.GetString(wire));
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Console.SetIn(originalIn);
		}
	}
}
