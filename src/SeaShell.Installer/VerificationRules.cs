using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>
/// The pass/fail rules of post-install verification, kept free of registry,
/// process and IPC calls so each rule can be asserted on its own. The
/// verifier collects the inputs and reports; these decide.
/// </summary>
public static class VerificationRules
{
	/// <summary>
	/// True when the <c>current</c> junction resolves to the version directory
	/// this pass just activated. Comparison is normalized (absolute, no
	/// trailing separator) and case-insensitive, matching how Windows reports
	/// junction targets back.
	/// </summary>
	public static bool JunctionPointsToVersion(string? junctionTarget, string versionDir)
	{
		if (junctionTarget == null) return false;
		return string.Equals(
			Path.GetFullPath(junctionTarget).TrimEnd(Path.DirectorySeparatorChar),
			Path.GetFullPath(versionDir).TrimEnd(Path.DirectorySeparatorChar),
			StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>True when <c>sea --version</c> output names the version just installed.</summary>
	public static bool SeaReportsVersion(string seaVersionOutput, string version) =>
		seaVersionOutput.Contains($"v{version}", StringComparison.Ordinal);

	/// <summary>True when an HKCU open-command registers the binary we just extracted.</summary>
	public static bool FileAssociationPointsTo(string command, string expectedExe) =>
		command.Contains(expectedExe, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True when a daemon answered and reports the version we started. A ping
	/// from a stale pipe of a different version does not count — that is the
	/// same-version-reinstall case where the old daemon has not gone yet.
	/// </summary>
	public static bool DaemonReportsVersion(string? pingedVersion, string expectedVersion) =>
		pingedVersion != null && pingedVersion == expectedVersion;
}