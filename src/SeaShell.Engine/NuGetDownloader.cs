using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace SeaShell.Engine;

/// <summary>
/// Downloads missing NuGet packages by running `dotnet restore` on a temporary project.
/// This leverages NuGet's own transitive dependency resolution — after restore completes,
/// the entire dependency tree is in ~/.nuget/packages/ and the .nuspec walker finds everything.
/// </summary>
public static class NuGetDownloader
{
	private static readonly ILogger _log = Log.ForContext(typeof(NuGetDownloader));

	public sealed record DownloadResult(bool Success, string? Error);

	/// <summary>
	/// Download a package (and all its transitive deps) into the global NuGet cache.
	/// </summary>
	public static DownloadResult Download(string packageName, string? version)
	{
		var tempDir = Path.Combine(Path.GetTempPath(), "seashell", "restore", Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(tempDir);

		try
		{
			var tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
			var versionAttr = $" Version=\"{version ?? "*"}\"";  // * = latest

			// Minimal project file — just enough for dotnet restore to resolve the package
			var csproj = $"""
				<Project Sdk="Microsoft.NET.Sdk">
					<PropertyGroup>
						<TargetFramework>{tfm}</TargetFramework>
					</PropertyGroup>
					<ItemGroup>
						<PackageReference Include="{packageName}"{versionAttr} />
					</ItemGroup>
				</Project>
				""";

			File.WriteAllText(Path.Combine(tempDir, "restore.csproj"), csproj);

			_log.Information("Downloading NuGet package {Package} {Version}", packageName, version ?? "latest");

			var psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				ArgumentList = { "restore", "--verbosity", "quiet" },
				WorkingDirectory = tempDir,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			using var proc = Process.Start(psi)!;
			var stdout = proc.StandardOutput.ReadToEnd();
			var stderr = proc.StandardError.ReadToEnd();
			proc.WaitForExit(60_000); // 60s timeout

			if (!proc.HasExited)
			{
				try { proc.Kill(); } catch { }
				return new DownloadResult(false, $"Package restore timed out after 60s");
			}

			if (proc.ExitCode != 0)
			{
				var output = $"{stdout}\n{stderr}".Trim();
				return new DownloadResult(false, $"dotnet restore failed:\n{output}");
			}

			_log.Information("Downloaded {Package}", packageName);
			return new DownloadResult(true, null);
		}
		catch (Exception ex)
		{
			return new DownloadResult(false, $"Package download failed: {ex.Message}");
		}
		finally
		{
			try { Directory.Delete(tempDir, recursive: true); } catch { }
		}
	}
}
