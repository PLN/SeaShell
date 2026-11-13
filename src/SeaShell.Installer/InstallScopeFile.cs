using System;
using System.IO;

namespace SeaShell.Installer;

/// <summary>What reading <c>{DataDir}/install.scope</c> found.</summary>
public enum ScopeFileState
{
	/// <summary>No file: an install made by 0.6.2 or earlier, or nothing installed.</summary>
	Missing,

	/// <summary>File read; <see cref="ScopeFileRead.Parts"/> holds its content.</summary>
	Read,

	/// <summary>The file exists but could not be read. Never treated as "user only".</summary>
	Unreadable,
}

public readonly record struct ScopeFileRead(ScopeFileState State, ScopeParts Parts, string? Error = null);

/// <summary>
/// The remembered install scope of one identity: <c>{DataDir}/install.scope</c>,
/// plain text, one token per line (see <see cref="ScopeTokens"/>). Written by
/// the user pass after a successful install; read by install, update and
/// uninstall when no scope flag is given.
/// </summary>
public static class InstallScopeFile
{
	public const string FileName = "install.scope";

	public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

	public static ScopeFileRead Read(string dataDir)
	{
		var path = PathIn(dataDir);
		try
		{
			if (!File.Exists(path)) return new ScopeFileRead(ScopeFileState.Missing, ScopeParts.None);
			return new ScopeFileRead(ScopeFileState.Read, ScopeTokens.ParseFile(File.ReadAllText(path)));
		}
		catch (Exception ex)
		{
			return new ScopeFileRead(ScopeFileState.Unreadable, ScopeParts.None, ex.Message);
		}
	}

	/// <summary>Write the scope. Throws on failure; the caller reports it.</summary>
	public static void Write(string dataDir, ScopeParts parts)
	{
		Directory.CreateDirectory(dataDir);
		File.WriteAllText(PathIn(dataDir), ScopeTokens.FormatFile(parts));
	}

	/// <summary>Remove the file (uninstall). True when it is gone afterwards.</summary>
	public static bool Delete(string dataDir)
	{
		try
		{
			File.Delete(PathIn(dataDir));
			return true;
		}
		catch { return !File.Exists(PathIn(dataDir)); }
	}
}
