using System.IO;
using SeaShell.Engine;
using Xunit;

namespace EngineTest;

/// <summary>
/// Regression tests for IncludeResolver v0.3 field merge.
/// The bug: IncludeResolver.ResolveRecursive merged WebApp/Elevate/Watch
/// but not Restart/MutexScope/MutexAttach/Window/Console from included files.
/// </summary>
public class IncludeResolverTests
{
	[Fact]
	public void Resolve_MergesRestartFromInclude()
	{
		var dir = CreateTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "main.cs"), "//sea_inc helper.cs\n");
			File.WriteAllText(Path.Combine(dir, "helper.cs"), "//sea_restart\n");

			var resolver = new IncludeResolver();
			var result = resolver.Resolve(Path.Combine(dir, "main.cs"));

			Assert.True(result.Directives.Restart);
		}
		finally { Directory.Delete(dir, true); }
	}

	[Fact]
	public void Resolve_MergesMutexScopeFromInclude()
	{
		var dir = CreateTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "main.cs"), "//sea_inc helper.cs\n");
			File.WriteAllText(Path.Combine(dir, "helper.cs"), "//sea_mutex user\n");

			var resolver = new IncludeResolver();
			var result = resolver.Resolve(Path.Combine(dir, "main.cs"));

			Assert.Equal(2, result.Directives.MutexScope); // 2 = User
		}
		finally { Directory.Delete(dir, true); }
	}

	[Fact]
	public void Resolve_MergesMutexAttachFromInclude()
	{
		var dir = CreateTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "main.cs"), "//sea_inc helper.cs\n");
			File.WriteAllText(Path.Combine(dir, "helper.cs"), "//sea_mutex_attach\n");

			var resolver = new IncludeResolver();
			var result = resolver.Resolve(Path.Combine(dir, "main.cs"));

			Assert.True(result.Directives.MutexAttach);
			Assert.True(result.Directives.MutexScope > 0); // mutex_attach implies mutex
		}
		finally { Directory.Delete(dir, true); }
	}

	[Fact]
	public void Resolve_MergesWindowAndConsoleFromInclude()
	{
		var dir = CreateTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "main.cs"), "//sea_inc helper.cs\n");
			File.WriteAllText(Path.Combine(dir, "helper.cs"), "//sea_window\n//sea_console\n");

			var resolver = new IncludeResolver();
			var result = resolver.Resolve(Path.Combine(dir, "main.cs"));

			Assert.True(result.Directives.Window);
			Assert.True(result.Directives.Console);
		}
		finally { Directory.Delete(dir, true); }
	}

	[Fact]
	public void Resolve_MutexScope_TakesHighest()
	{
		var dir = CreateTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "main.cs"),
				"//sea_mutex session\n//sea_inc helper.cs\n");
			File.WriteAllText(Path.Combine(dir, "helper.cs"), "//sea_mutex system\n");

			var resolver = new IncludeResolver();
			var result = resolver.Resolve(Path.Combine(dir, "main.cs"));

			Assert.Equal(3, result.Directives.MutexScope); // 3 = System (highest wins)
		}
		finally { Directory.Delete(dir, true); }
	}

	[Fact]
	public void Resolve_AllV03Fields_FromInclude()
	{
		var dir = CreateTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "main.cs"), "//sea_inc helper.cs\n");
			File.WriteAllText(Path.Combine(dir, "helper.cs"),
				"//sea_restart\n//sea_mutex user\n//sea_mutex_attach\n//sea_window\n//sea_console\n");

			var resolver = new IncludeResolver();
			var result = resolver.Resolve(Path.Combine(dir, "main.cs"));

			Assert.True(result.Directives.Restart);
			Assert.True(result.Directives.MutexScope >= 2); // mutex_attach may override to system
			Assert.True(result.Directives.MutexAttach);
			Assert.True(result.Directives.Window);
			Assert.True(result.Directives.Console);
		}
		finally { Directory.Delete(dir, true); }
	}

	private static string CreateTempDir()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"seashell-test-{System.Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		return dir;
	}
}
