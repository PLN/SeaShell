using System;

namespace SeaShell.Installer;

/// <summary>Reading <c>dotnet --list-sdks</c> output.</summary>
public static class DotnetSdk
{
	/// <summary>
	/// True when a listed SDK has at least <paramref name="minMajor"/> as its
	/// major version. Lines look like <c>10.0.103 [C:\Program Files\dotnet\sdk]</c>.
	/// </summary>
	public static bool HasSdk(string listSdksOutput, int minMajor)
	{
		foreach (var line in listSdksOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var space = line.IndexOf(' ');
			var versionText = space > 0 ? line[..space] : line;
			var dash = versionText.IndexOf('-');                 // 11.0.100-preview.1
			if (dash > 0) versionText = versionText[..dash];
			if (Version.TryParse(versionText, out var v) && v.Major >= minMajor) return true;
		}
		return false;
	}
}
