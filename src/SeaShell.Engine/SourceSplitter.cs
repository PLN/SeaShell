using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SeaShell.Engine;

/// <summary>
/// Splits C# source files that mix top-level statements and type declarations
/// into separate syntax trees so Roslyn can compile them cleanly.
/// </summary>
static class SourceSplitter
{
	/// <summary>
	/// If a source file mixes top-level statements and type declarations, split it
	/// into two syntax trees: one with types (+ usings), one with top-level statements
	/// (+ usings). This frees the user from the "no top-level statements after class
	/// definitions" restriction.
	///
	/// If the source only has one kind, returns the original tree unchanged.
	/// </summary>
	public static List<SyntaxTree> SplitTopLevelAndTypes(
		SyntaxTree tree, CSharpParseOptions opts, string path)
	{
		var root = tree.GetCompilationUnitRoot();
		var members = root.Members;

		var hasTopLevel = members.OfType<GlobalStatementSyntax>().Any();
		var hasTypes = members.Any(m => m is TypeDeclarationSyntax or NamespaceDeclarationSyntax
			or FileScopedNamespaceDeclarationSyntax or EnumDeclarationSyntax
			or DelegateDeclarationSyntax or RecordDeclarationSyntax);

		// No mixing — return as-is
		if (!hasTopLevel || !hasTypes)
			return new List<SyntaxTree> { tree };

		// Collect the text pieces. We preserve usings/externs for both trees.
		var usings = string.Join("\n", root.Usings.Select(u => u.ToFullString()));
		var externs = string.Join("\n", root.Externs.Select(e => e.ToFullString()));
		var preamble = externs + (externs.Length > 0 ? "\n" : "") + usings + (usings.Length > 0 ? "\n" : "");

		var topLevelLines = new List<string>();
		var typeLines = new List<string>();

		foreach (var member in members)
		{
			if (member is GlobalStatementSyntax)
				topLevelLines.Add(member.ToFullString());
			else
				typeLines.Add(member.ToFullString());
		}

		var result = new List<SyntaxTree>();

		// Types tree (classes, records, enums, etc.) — goes first so it's available to top-level code
		if (typeLines.Count > 0)
		{
			var typeSource = preamble + string.Join("", typeLines);
			result.Add(CSharpSyntaxTree.ParseText(typeSource, opts, path: path + ".types"));
		}

		// Top-level statements tree — goes last (this becomes the entry point)
		if (topLevelLines.Count > 0)
		{
			var topSource = preamble + string.Join("", topLevelLines);
			result.Add(CSharpSyntaxTree.ParseText(topSource, opts, path: path));
		}

		return result;
	}
}
