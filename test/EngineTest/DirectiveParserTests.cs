using System.Linq;
using Xunit;
using SeaShell.Engine;
using static SeaShell.Engine.DirectiveParser;

namespace SeaShell.Engine.Tests;

public class DirectiveParserTests
{
	// ── Happy-Path Directives ──────────────────────────────────────

	[Fact]
	public void Parse_Inc()
	{
		var result = DirectiveParser.Parse("//sea_inc foo.cs");
		Assert.Single(result.Includes);
		Assert.Equal("foo.cs", result.Includes[0]);
	}

	[Fact]
	public void Parse_IncDir()
	{
		var result = DirectiveParser.Parse("//sea_incdir lib/");
		Assert.Single(result.IncludeDirs);
		// ExpandPath normalizes separators for current platform
		Assert.Equal("lib" + System.IO.Path.DirectorySeparatorChar, result.IncludeDirs[0]);
	}

	[Fact]
	public void Parse_Ref()
	{
		var result = DirectiveParser.Parse("//sea_ref System.Net.Http");
		Assert.Single(result.References);
		Assert.Equal("System.Net.Http", result.References[0]);
	}

	[Fact]
	public void Parse_NuGet_WithVersion()
	{
		var result = DirectiveParser.Parse("//sea_nuget Serilog 4.0.0");
		Assert.Single(result.NuGets);
		Assert.Equal("Serilog", result.NuGets[0].PackageName);
		Assert.Equal("4.0.0", result.NuGets[0].Version);
	}

	[Fact]
	public void Parse_NuGet_WithoutVersion()
	{
		var result = DirectiveParser.Parse("//sea_nuget Serilog");
		Assert.Single(result.NuGets);
		Assert.Equal("Serilog", result.NuGets[0].PackageName);
		Assert.Null(result.NuGets[0].Version);
	}

	[Fact]
	public void Parse_WebApp()
	{
		var result = DirectiveParser.Parse("//sea_webapp");
		Assert.True(result.WebApp);
	}

	[Fact]
	public void Parse_Elevate()
	{
		var result = DirectiveParser.Parse("//sea_elevate");
		Assert.True(result.Elevate);
	}

	[Fact]
	public void Parse_Watch()
	{
		var result = DirectiveParser.Parse("//sea_watch");
		Assert.True(result.Watch);
	}

	[Fact]
	public void Parse_MultipleIncludes()
	{
		var result = DirectiveParser.Parse("//sea_inc a.cs\n//sea_inc b.cs\n//sea_inc c.cs");
		Assert.Equal(3, result.Includes.Count);
		Assert.Equal(new[] { "a.cs", "b.cs", "c.cs" }, result.Includes);
	}

	[Fact]
	public void Parse_CssPrefix()
	{
		var result = DirectiveParser.Parse("//css_inc foo.cs");
		Assert.Single(result.Includes);
		Assert.Equal("foo.cs", result.Includes[0]);
	}

	// ── Whitespace Edge Cases ──────────────────────────────────────

	[Fact]
	public void Parse_LeadingSpaces_Matches()
	{
		var result = DirectiveParser.Parse("   //sea_inc foo.cs");
		Assert.Single(result.Includes);
		Assert.Equal("foo.cs", result.Includes[0]);
	}

	[Fact]
	public void Parse_LeadingTab_Matches()
	{
		var result = DirectiveParser.Parse("\t//sea_inc foo.cs");
		Assert.Single(result.Includes);
		Assert.Equal("foo.cs", result.Includes[0]);
	}

	[Fact]
	public void Parse_TrailingWhitespace_Trimmed()
	{
		var result = DirectiveParser.Parse("//sea_inc foo.cs   ");
		Assert.Single(result.Includes);
		Assert.Equal("foo.cs", result.Includes[0]);
	}

	[Fact]
	public void Parse_MultipleSpacesBeforeArg()
	{
		var result = DirectiveParser.Parse("//sea_inc   foo.cs");
		Assert.Single(result.Includes);
		Assert.Equal("foo.cs", result.Includes[0]);
	}

	[Fact]
	public void Parse_WhitespaceInsidePrefix_NotDirective()
	{
		// "// sea_inc" has a space breaking the prefix — treated as comment
		var result = DirectiveParser.Parse("// sea_inc foo.cs\n//sea_inc real.cs");
		Assert.Single(result.Includes);
		Assert.Equal("real.cs", result.Includes[0]);
	}

	// ── Non-Directive Comments — Must NOT Match ────────────────────

	[Theory]
	[InlineData("// //sea_inc foo.cs")]       // directive inside comment (cs-script bug)
	[InlineData("/// //sea_inc foo.cs")]      // doc comment
	[InlineData("// //css_inc foo.cs")]       // css compat inside comment
	[InlineData("//SEA_inc foo.cs")]          // wrong case
	[InlineData("// sea_inc foo.cs")]         // space breaks prefix
	public void Parse_CommentLine_SkippedButDoesNotStopScanning(string commentLine)
	{
		var source = commentLine + "\n//sea_inc real.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Single(result.Includes);
		Assert.Equal("real.cs", result.Includes[0]);
	}

	// ── Lines That Stop Scanning ───────────────────────────────────

	[Theory]
	[InlineData("/* block comment */")]
	[InlineData("var x = 1;")]
	[InlineData("Console.WriteLine();")]
	[InlineData("namespace Foo;")]
	public void Parse_CodeLine_StopsScanning(string codeLine)
	{
		var source = codeLine + "\n//sea_inc after.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Empty(result.Includes);
	}

	// ── Scanning Boundaries ────────────────────────────────────────

	[Fact]
	public void Parse_DirectiveAfterCode_NotParsed()
	{
		var source = "//sea_inc before.cs\nvar x = 1;\n//sea_inc after.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Single(result.Includes);
		Assert.Equal("before.cs", result.Includes[0]);
	}

	[Fact]
	public void Parse_BlankLinesBetween_AllParsed()
	{
		var source = "//sea_inc a.cs\n\n\n//sea_inc b.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Equal(2, result.Includes.Count);
		Assert.Equal(new[] { "a.cs", "b.cs" }, result.Includes);
	}

	[Fact]
	public void Parse_UsingsSkipped_DirectivesStillParsed()
	{
		var source = "//sea_inc a.cs\nusing System;\n//sea_inc b.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Equal(2, result.Includes.Count);
	}

	[Fact]
	public void Parse_GlobalUsingsSkipped()
	{
		var source = "//sea_inc a.cs\nglobal using System;\n//sea_inc b.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Equal(2, result.Includes.Count);
	}

	[Fact]
	public void Parse_AttributesSkipped()
	{
		var source = "//sea_inc a.cs\n[assembly: System.CLSCompliant(true)]\n//sea_inc b.cs";
		var result = DirectiveParser.Parse(source);
		Assert.Equal(2, result.Includes.Count);
	}

	[Fact]
	public void Parse_EmptySource()
	{
		var result = DirectiveParser.Parse("");
		Assert.Empty(result.Includes);
		Assert.Empty(result.IncludeDirs);
		Assert.Empty(result.NuGets);
		Assert.Empty(result.References);
		Assert.False(result.WebApp);
		Assert.False(result.Elevate);
		Assert.False(result.Watch);
	}

	// ── NuGet Variants ─────────────────────────────────────────────

	[Theory]
	[InlineData("//sea_nuget Serilog 4.0.0", "Serilog", "4.0.0")]
	[InlineData("//sea_nuget Serilog, 4.0.0", "Serilog", "4.0.0")]
	[InlineData("//sea_nuget Serilog", "Serilog", null)]
	[InlineData("//sea_nuget  Serilog  4.0.0", "Serilog", "4.0.0")]
	public void Parse_NuGet_Variants(string source, string expectedPkg, string? expectedVer)
	{
		var result = DirectiveParser.Parse(source);
		Assert.Single(result.NuGets);
		Assert.Equal(expectedPkg, result.NuGets[0].PackageName);
		Assert.Equal(expectedVer, result.NuGets[0].Version);
	}

	// ── Ignored / Unknown ──────────────────────────────────────────

	[Fact]
	public void Parse_UnknownDirective_Ignored()
	{
		var result = DirectiveParser.Parse("//sea_unknown arg");
		Assert.Empty(result.Includes);
		Assert.Empty(result.NuGets);
		Assert.False(result.WebApp);
	}

	[Fact]
	public void Parse_EmptyArg_NotAdded()
	{
		var result = DirectiveParser.Parse("//sea_inc");
		Assert.Empty(result.Includes);
	}

	[Fact]
	public void Parse_EmptyArg_TrailingSpace()
	{
		var result = DirectiveParser.Parse("//sea_inc ");
		Assert.Empty(result.Includes);
	}

	// ── Restart ────────────────────────────────────────────────────

	[Fact]
	public void Parse_Restart()
	{
		var result = DirectiveParser.Parse("//sea_restart");
		Assert.True(result.Restart);
	}

	// ── Mutex ──────────────────────────────────────────────────────

	[Fact]
	public void Parse_Mutex_DefaultSystem()
	{
		var result = DirectiveParser.Parse("//sea_mutex");
		Assert.Equal(3, result.MutexScope); // 3 = System
	}

	[Theory]
	[InlineData("//sea_mutex session", 1)]
	[InlineData("//sea_mutex user", 2)]
	[InlineData("//sea_mutex system", 3)]
	public void Parse_Mutex_Scope(string source, byte expectedScope)
	{
		var result = DirectiveParser.Parse(source);
		Assert.Equal(expectedScope, result.MutexScope);
	}

	[Fact]
	public void Parse_MutexAttach_ImpliesMutex()
	{
		var result = DirectiveParser.Parse("//sea_mutex_attach");
		Assert.True(result.MutexAttach);
		Assert.Equal(3, result.MutexScope); // default System
	}

	[Fact]
	public void Parse_MutexAttach_WithScope()
	{
		var result = DirectiveParser.Parse("//sea_mutex_attach user");
		Assert.True(result.MutexAttach);
		Assert.Equal(2, result.MutexScope); // User
	}

	// ── Window / Console ──────────────────────────────────────────

	[Fact]
	public void Parse_Window()
	{
		var result = DirectiveParser.Parse("//sea_window");
		Assert.True(result.Window);
	}

	[Fact]
	public void Parse_Console()
	{
		var result = DirectiveParser.Parse("//sea_console");
		Assert.True(result.Console);
	}

	// ── Multiple new directives combined ──────────────────────────

	[Fact]
	public void Parse_AllNewDirectives()
	{
		var source = "//sea_restart\n//sea_mutex user\n//sea_window";
		var result = DirectiveParser.Parse(source);
		Assert.True(result.Restart);
		Assert.Equal(2, result.MutexScope); // User
		Assert.True(result.Window);
	}

	// ── VB Support ─────────────────────────────────────────────────

	[Fact]
	public void Parse_VB_Inc()
	{
		var result = DirectiveParser.Parse("'sea_inc foo.vb", ScriptLanguage.VisualBasic);
		Assert.Single(result.Includes);
		Assert.Equal("foo.vb", result.Includes[0]);
	}

	[Fact]
	public void Parse_VB_CssPrefix()
	{
		var result = DirectiveParser.Parse("'css_inc foo.vb", ScriptLanguage.VisualBasic);
		Assert.Single(result.Includes);
		Assert.Equal("foo.vb", result.Includes[0]);
	}

	[Fact]
	public void Parse_VB_DirectiveInsideComment_Skipped()
	{
		var source = "' 'sea_inc foo.vb\n'sea_inc real.vb";
		var result = DirectiveParser.Parse(source, ScriptLanguage.VisualBasic);
		Assert.Single(result.Includes);
		Assert.Equal("real.vb", result.Includes[0]);
	}

	[Fact]
	public void Parse_VB_RemComment_Skipped()
	{
		var source = "REM 'sea_inc foo.vb\n'sea_inc real.vb";
		var result = DirectiveParser.Parse(source, ScriptLanguage.VisualBasic);
		Assert.Single(result.Includes);
		Assert.Equal("real.vb", result.Includes[0]);
	}

	[Fact]
	public void Parse_VB_CSharpDirective_AlsoParsed()
	{
		// //sea_inc is recognized as a C# directive even in VB mode
		// (the directive check is language-independent)
		var source = "//sea_inc foo.cs\n'sea_inc real.vb";
		var result = DirectiveParser.Parse(source, ScriptLanguage.VisualBasic);
		Assert.Equal(2, result.Includes.Count);
		Assert.Equal(new[] { "foo.cs", "real.vb" }, result.Includes);
	}
}
