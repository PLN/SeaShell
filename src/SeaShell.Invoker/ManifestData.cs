using System.Collections.Generic;

namespace SeaShell.Invoker;

/// <summary>
/// Deserialization target for .sea.json manifest files.
/// Previously duplicated in both ScriptRunner (CLI) and ScriptHost (Host).
/// </summary>
public sealed class ManifestData
{
	public string? ScriptPath { get; set; }
	public string[]? Sources { get; set; }
	public Dictionary<string, string>? Packages { get; set; }
	public string[]? Assemblies { get; set; }
}
