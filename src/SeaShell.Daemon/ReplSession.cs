using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using SeaShell.Protocol;
using SeaShell.Engine;

namespace SeaShell.Daemon;

/// <summary>
/// A persistent REPL session backed by Roslyn's CSharpScript API.
/// Maintains ScriptState across evaluations — variables, usings, and
/// method definitions carry forward.
/// </summary>
public sealed class ReplSession
{
	private ScriptState<object>? _state;
	private readonly ScriptOptions _options;
	private readonly NuGetResolver _nugetResolver;
	private string _pendingCode = "";

	public ReplSession(NuGetResolver nugetResolver, string[] initialPackages)
	{
		_nugetResolver = nugetResolver;

		// For REPL, use assemblies already loaded in the daemon process — these are
		// runtime assemblies (not ref assemblies) and are guaranteed to be loadable.
		// This is different from the compiler which uses ref assemblies for compilation.
		var refs = AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
			.Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
			.ToList();

		// Also add the full runtime dir for types not yet loaded by the daemon
		var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
		var loadedPaths = new HashSet<string>(refs.Select(r => ((PortableExecutableReference)r).FilePath!),
			StringComparer.OrdinalIgnoreCase);
		foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
		{
			if (loadedPaths.Contains(dll)) continue;
			try
			{
				// Quick filter: skip known native DLLs
				var name = Path.GetFileNameWithoutExtension(dll);
				if (name is "coreclr" or "clrjit" or "clrgc" or "clrgcexp" or "clretwrc"
					or "hostpolicy" or "mscorrc" or "mscordbi" or "mscordaccore" or "msquic")
					continue;
				if (name.StartsWith("mscordaccore_") || name.StartsWith("Microsoft.DiaSymReader.Native")
					|| name.Contains("Compression.Native"))
					continue;
				refs.Add(MetadataReference.CreateFromFile(dll));
			}
			catch { }
		}

		// SeaShell.Script assembly
		var scriptDll = Path.Combine(AppContext.BaseDirectory, "SeaShell.Script.dll");
		if (File.Exists(scriptDll) && !loadedPaths.Contains(scriptDll))
			refs.Add(MetadataReference.CreateFromFile(scriptDll));

		// Resolve initial NuGet packages
		var resolvedPackages = new List<NuGetResolver.ResolvedPackage>();
		foreach (var pkgName in initialPackages)
		{
			var tree = nugetResolver.ResolveWithDependencies(pkgName, null);
			foreach (var pkg in tree)
				resolvedPackages.Add(pkg);
		}

		foreach (var pkg in resolvedPackages)
		{
			foreach (var dll in pkg.CompileDlls)
			{
				var bytes = File.ReadAllBytes(dll);
				refs.Add(MetadataReference.CreateFromImage(bytes, filePath: dll));
			}
		}

		_options = ScriptOptions.Default
			.WithLanguageVersion(LanguageVersion.Latest)
			.WithReferences(refs)
			.WithImports(
				"System",
				"System.Collections.Generic",
				"System.IO",
				"System.Linq",
				"System.Threading.Tasks",
				"SeaShell")
			.WithAllowUnsafe(true);
	}

	public async Task<ReplEvalResponse> EvalAsync(string code)
	{
		// Append to pending code (for multi-line input)
		_pendingCode += (_pendingCode.Length > 0 ? "\n" : "") + code;

		// Check if the code is complete (no unclosed braces, etc.)
		var tree = CSharpSyntaxTree.ParseText(_pendingCode, CSharpParseOptions.Default
			.WithLanguageVersion(LanguageVersion.Latest)
			.WithKind(SourceCodeKind.Script));

		var diags = tree.GetDiagnostics().ToList();
		var hasIncomplete = diags.Any(d =>
			d.Id == "CS1733" ||  // Expected expression
			d.Id == "CS1513" ||  // } expected
			d.Id == "CS1002" ||  // ; expected (sometimes from incomplete)
			d.Id == "CS1026" ||  // ) expected
			d.Id == "CS1003");   // Syntax error, ',' expected

		// Heuristic: if there are unmatched braces, it's incomplete
		var opens = _pendingCode.Count(c => c == '{') + _pendingCode.Count(c => c == '(');
		var closes = _pendingCode.Count(c => c == '}') + _pendingCode.Count(c => c == ')');
		if (opens > closes)
		{
			return new ReplEvalResponse(true, null, null, null, false);
		}

		// We have complete code — evaluate it
		var fullCode = _pendingCode;
		_pendingCode = "";

		try
		{
			// Capture Console output
			var oldOut = Console.Out;
			var oldErr = Console.Error;
			using var sw = new StringWriter();
			Console.SetOut(sw);
			Console.SetError(sw);

			try
			{
				if (_state == null)
					_state = await CSharpScript.RunAsync<object>(fullCode, _options);
				else
					_state = await _state.ContinueWithAsync<object>(fullCode);
			}
			finally
			{
				Console.SetOut(oldOut);
				Console.SetError(oldErr);
			}

			var output = sw.ToString();
			var result = _state.ReturnValue;
			var resultStr = result != null ? FormatResult(result) : null;

			return new ReplEvalResponse(true, resultStr,
				string.IsNullOrEmpty(output) ? null : output,
				null, true);
		}
		catch (CompilationErrorException ex)
		{
			return new ReplEvalResponse(false, null, null,
				string.Join("\n", ex.Diagnostics.Select(d => d.ToString())),
				true);
		}
		catch (Exception ex)
		{
			return new ReplEvalResponse(false, null, null,
				$"{ex.GetType().Name}: {ex.Message}",
				true);
		}
	}

	/// <summary>Add a NuGet package to the session at runtime.</summary>
	public ReplEvalResponse AddPackage(string packageName, string? version)
	{
		try
		{
			// Download if needed
			if (_nugetResolver.Resolve(packageName, version) == null)
			{
				var dl = NuGetDownloader.Download(packageName, version);
				if (!dl.Success)
					return new ReplEvalResponse(false, null, null, dl.Error, true);
				_nugetResolver.InvalidateCache();
			}

			var tree = _nugetResolver.ResolveWithDependencies(packageName, version);
			if (tree.Count == 0)
				return new ReplEvalResponse(false, null, null,
					$"Package '{packageName}' could not be resolved.", true);

			// We can't add references to an existing ScriptState, so we note this
			// limitation. For now, packages must be specified at session start.
			return new ReplEvalResponse(true,
				$"Package '{packageName}' is available. Note: restart REPL with `sea --repl {packageName}` to use it.",
				null, null, true);
		}
		catch (Exception ex)
		{
			return new ReplEvalResponse(false, null, null, ex.Message, true);
		}
	}

	private static string FormatResult(object result)
	{
		if (result is string s) return $"\"{s}\"";
		return result.ToString() ?? "null";
	}

}
