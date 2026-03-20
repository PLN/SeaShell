using System;
using System.Threading.Tasks;
using SeaShell.Ipc;
using SeaShell.Protocol;

namespace SeaShell.Cli;

static class ReplClient
{
	/// <summary>Full interactive REPL loop: connect to daemon, send/receive eval requests.</summary>
	public static async Task<int> ReplAsync(string daemonAddress, string[] packages)
	{
		// Ensure daemon is running
		if (!await TransportClient.ProbeAsync(daemonAddress))
		{
			DaemonManager.StartDaemon();
			for (int i = 0; i < 20; i++)
			{
				await Task.Delay(250);
				if (await TransportClient.ProbeAsync(daemonAddress))
					break;
			}
			if (!await TransportClient.ProbeAsync(daemonAddress))
			{
				Console.Error.WriteLine("sea: daemon failed to start");
				return 1;
			}
		}

		await using var conn = await TransportClient.ConnectAsync(daemonAddress, timeoutMs: 10000);

		// Start REPL session
		await conn.SendAsync(Envelope.Wrap(new ReplStartRequest(packages)).ToBytes());
		var startBytes = await conn.ReceiveAsync();
		if (startBytes == null) { Console.Error.WriteLine("sea: daemon disconnected"); return 1; }

		var startResp = Envelope.FromBytes(startBytes).Unwrap<ReplStartResponse>();
		if (!startResp.Success) { Console.Error.WriteLine(startResp.Error); return 1; }

		// Banner
		Console.WriteLine($"SeaShell REPL (.NET {Environment.Version})");
		if (packages.Length > 0)
			Console.WriteLine($"Packages: {string.Join(", ", packages)}");
		Console.WriteLine("Type C# expressions and statements. .exit to quit, .clear to reset.");
		Console.WriteLine();

		// Input loop
		var continuing = false;
		while (true)
		{
			Console.Write(continuing ? "...> " : "sea> ");
			var line = Console.ReadLine();
			if (line == null) break; // EOF (Ctrl+Z on Windows, Ctrl+D on Linux)

			// Dot commands
			if (!continuing && line.TrimStart().StartsWith("."))
			{
				var cmd = line.Trim().ToLowerInvariant();
				if (cmd == ".exit" || cmd == ".quit") break;
				if (cmd == ".clear")
				{
					Console.Clear();
					continue;
				}
				if (cmd == ".help")
				{
					Console.WriteLine("  .exit     Exit REPL");
					Console.WriteLine("  .clear    Clear screen");
					Console.WriteLine("  .help     Show this help");
					continue;
				}
			}

			// Send to daemon for evaluation
			await conn.SendAsync(Envelope.Wrap(new ReplEvalRequest(line)).ToBytes());
			var evalBytes = await conn.ReceiveAsync();
			if (evalBytes == null) { Console.Error.WriteLine("\nsea: daemon disconnected"); return 1; }

			var eval = Envelope.FromBytes(evalBytes).Unwrap<ReplEvalResponse>();

			if (!eval.IsComplete)
			{
				// Incomplete — need more input
				continuing = true;
				continue;
			}

			continuing = false;

			// Print captured output
			if (eval.Output != null)
				Console.Write(eval.Output);

			// Print result
			if (eval.Success && eval.Result != null)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine(eval.Result);
				Console.ResetColor();
			}

			// Print error
			if (!eval.Success && eval.Error != null)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine(eval.Error);
				Console.ResetColor();
			}
		}

		return 0;
	}
}
