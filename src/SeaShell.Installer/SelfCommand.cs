using System;
using System.Collections.Generic;
using System.IO;

namespace SeaShell.Installer;

/// <summary>How to start this bootstrapper again (self-elevation, sudo pass).</summary>
public sealed record SelfCommandLine(string FileName, IReadOnlyList<string> PrefixArguments);

/// <summary>
/// Rebuild the command that runs this bootstrapper. A global tool or the
/// installed copy runs from its apphost, which is the process path. Under
/// <c>dnx</c> or <c>dotnet seashell.dll</c> the process path is the dotnet
/// host itself, and relaunching that without the entry assembly would start
/// a bare <c>dotnet</c>.
/// </summary>
public static class SelfCommand
{
	/// <summary>Null when the command cannot be determined.</summary>
	public static SelfCommandLine? Build(string? processPath, string? entryAssemblyPath)
	{
		if (string.IsNullOrEmpty(processPath)) return null;

		if (IsDotnetHost(processPath))
		{
			if (string.IsNullOrEmpty(entryAssemblyPath)) return null;
			return new SelfCommandLine(processPath, new[] { entryAssemblyPath });
		}

		return new SelfCommandLine(processPath, Array.Empty<string>());
	}

	/// <summary>True when the path is the <c>dotnet</c> muxer rather than an apphost.</summary>
	public static bool IsDotnetHost(string processPath) =>
		string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
}
