using System;
using System.IO;
using System.Linq;
using SeaShell.Installer;

namespace SeaShell.Bootstrapper;

/// <summary>Checks that run before an install or update changes anything on the machine.</summary>
internal static class Preflight
{
	/// <summary>
	/// The .NET 10 SDK must be present: the daemon resolves the NuGet
	/// packages of scripts through it, and <c>seashell update</c> fetches
	/// SeaShell itself with <c>dotnet tool install</c>. False (with the
	/// explanation printed) stops the command.
	/// </summary>
	public static bool SdkPresent()
	{
		var dotnet = DotnetHost.Resolve();
		var output = EnvironmentProbe.RunProcess(dotnet, "--list-sdks");
		if (DotnetSdk.HasSdk(output, 10)) return true;

		Console.Error.WriteLine($"  ERROR: no .NET 10 SDK found ('{dotnet} --list-sdks' listed {(string.IsNullOrWhiteSpace(output) ? "nothing" : "only: " + string.Join(", ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))}).");
		Console.Error.WriteLine("         SeaShell downloads the NuGet packages of scripts through the SDK, and");
		Console.Error.WriteLine("         'seashell update' fetches SeaShell itself with 'dotnet tool install'.");
		Console.Error.WriteLine("         Install the .NET 10 SDK (https://dot.net) and run this again.");
		return false;
	}

	/// <summary>
	/// Linux: <c>sea</c> and <c>seashell</c> are symlinked into
	/// <c>~/.local/bin</c>; say exactly what to add when that is not on PATH.
	/// </summary>
	public static void LinuxPathHint()
	{
		if (OperatingSystem.IsWindows()) return;
		var localBin = PathEnvironment.LocalBinDir();
		var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(':', StringSplitOptions.RemoveEmptyEntries)
			.Any(d => string.Equals(d.TrimEnd('/'), localBin.TrimEnd('/'), StringComparison.Ordinal));
		if (onPath) return;

		Console.WriteLine($"  NOTE: {localBin} is not on PATH, so 'sea' and 'seashell' will not be found there.");
		Console.WriteLine("        Add this line to ~/.profile (or your shell's rc file) and open a new terminal:");
		Console.WriteLine("          export PATH=\"$HOME/.local/bin:$PATH\"");
	}
}
