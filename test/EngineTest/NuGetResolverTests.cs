using System;
using System.IO;
using System.Linq;
using SeaShell.Engine;
using Xunit;

namespace SeaShell.Engine.Tests;

public sealed class NuGetResolverTests : IDisposable
{
	private readonly string _cacheRoot;

	public NuGetResolverTests()
	{
		_cacheRoot = Path.Combine(Path.GetTempPath(), $"seashell-nuget-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_cacheRoot);
	}

	public void Dispose()
	{
		if (Directory.Exists(_cacheRoot))
			Directory.Delete(_cacheRoot, recursive: true);
	}

	[Fact]
	public void ResolveWithDependencies_DirectVersionWinsOverTransitive()
	{
		WritePackage("Root.Package", "1.0.0", ("System.Runtime.Caching", "[6.0.0, )"));
		WritePackage("System.Runtime.Caching", "6.0.0");
		WritePackage("System.Runtime.Caching", "8.0.1");

		var resolver = new NuGetResolver(_cacheRoot, "win-x64");
		var directRefs = new (string PackageName, string? Version)[]
		{
			("Root.Package", "1.0.0"),
			("System.Runtime.Caching", "8.0.1"),
		};
		var packages = resolver.ResolveWithDependencies(directRefs);

		var caching = packages.Single(p => p.Name.Equals("System.Runtime.Caching", StringComparison.OrdinalIgnoreCase));
		Assert.Equal("8.0.1", caching.Version);
		Assert.DoesNotContain(packages, p =>
			p.Name.Equals("System.Runtime.Caching", StringComparison.OrdinalIgnoreCase)
			&& p.Version == "6.0.0");
	}

	[Fact]
	public void ResolveWithDependencies_UpgradesRepeatedTransitiveToHigherRequest()
	{
		WritePackage("Root.Package", "1.0.0",
			("Uses.Low", "[1.0.0, )"),
			("Uses.High", "[1.0.0, )"));
		WritePackage("Uses.Low", "1.0.0", ("Shared.Dependency", "[1.0.0, )"));
		WritePackage("Uses.High", "1.0.0", ("Shared.Dependency", "[2.0.0, )"));
		WritePackage("Shared.Dependency", "1.0.0");
		WritePackage("Shared.Dependency", "2.0.0");

		var resolver = new NuGetResolver(_cacheRoot, "win-x64");
		var packages = resolver.ResolveWithDependencies("Root.Package", "1.0.0");

		var shared = packages.Single(p => p.Name.Equals("Shared.Dependency", StringComparison.OrdinalIgnoreCase));
		Assert.Equal("2.0.0", shared.Version);
	}

	[Fact]
	public void Resolve_PrefersRuntimeNet6AssetOverGenericNetstandardShim()
	{
		WritePackage("Shimmed.Package", "1.0.0");
		WriteDll("Shimmed.Package", "1.0.0", "lib/netstandard2.0/Shimmed.Package.dll");
		WriteDll("Shimmed.Package", "1.0.0", "runtimes/win/lib/net6.0/Shimmed.Package.dll");

		var resolver = new NuGetResolver(_cacheRoot, "win-x64");
		var package = resolver.Resolve("Shimmed.Package", "1.0.0")!;

		Assert.Equal("runtimes/win/lib/net6.0/Shimmed.Package.dll", package.RuntimeAssets.Single().RelativePath);
		Assert.Equal("runtimes/win/lib/net6.0/Shimmed.Package.dll", Path.GetRelativePath(
			Path.Combine(_cacheRoot, "shimmed.package", "1.0.0"),
			package.CompileDlls.Single()).Replace('\\', '/'));
	}

	[Fact]
	public void Resolve_PrefersGenericNet6AssetOverRuntimeNetstandardShim()
	{
		WritePackage("Shimmed.Package", "1.0.0");
		WriteDll("Shimmed.Package", "1.0.0", "lib/net6.0/Shimmed.Package.dll");
		WriteDll("Shimmed.Package", "1.0.0", "runtimes/win/lib/netstandard2.0/Shimmed.Package.dll");

		var resolver = new NuGetResolver(_cacheRoot, "win-x64");
		var package = resolver.Resolve("Shimmed.Package", "1.0.0")!;

		Assert.Equal("lib/net6.0/Shimmed.Package.dll", package.RuntimeAssets.Single().RelativePath);
		Assert.Equal("lib/net6.0/Shimmed.Package.dll", Path.GetRelativePath(
			Path.Combine(_cacheRoot, "shimmed.package", "1.0.0"),
			package.CompileDlls.Single()).Replace('\\', '/'));
	}

	private void WritePackage(string id, string version, params (string Id, string VersionRange)[] dependencies)
	{
		var versionDir = Path.Combine(_cacheRoot, id.ToLowerInvariant(), version);
		Directory.CreateDirectory(versionDir);

		var deps = string.Join(Environment.NewLine, dependencies.Select(d =>
			$"""        <dependency id="{d.Id}" version="{d.VersionRange}" />"""));
		var nuspec = $$"""
			<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
			  <metadata>
			    <id>{{id}}</id>
			    <version>{{version}}</version>
			    <dependencies>
			{{deps}}
			    </dependencies>
			  </metadata>
		</package>
		""";
		File.WriteAllText(Path.Combine(versionDir, $"{id}.nuspec"), nuspec);
	}

	private void WriteDll(string id, string version, string relativePath)
	{
		var path = Path.Combine(_cacheRoot, id.ToLowerInvariant(), version, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, new byte[] { 0x4d, 0x5a });
	}
}
