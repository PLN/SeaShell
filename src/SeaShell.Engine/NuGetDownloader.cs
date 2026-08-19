using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

	private const int RestoreTimeoutMs = 60_000;
	private const int PipeDrainTimeoutMs = 5_000;

	public sealed record DownloadResult(bool Success, string? Error);

	/// <summary>
	/// Download a package (and all its transitive deps) into the global NuGet cache.
	/// </summary>
	public static DownloadResult Download(string packageName, string? version)
	{
		// Per-user scratch dir. NOT Path.GetTempPath(): on Linux /tmp is shared
		// between users, so a root-owned /tmp/seashell/ left behind by an elevated
		// run makes CreateDirectory fail for every non-root daemon afterwards.
		var tempDir = Path.Combine(SeaShellPaths.DataDir, "restore", Guid.NewGuid().ToString("N")[..8]);

		try
		{
			Directory.CreateDirectory(tempDir);
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
				// --disable-build-servers: no MSBuild/Roslyn worker processes that
				// could outlive the restore holding inherited pipe handles.
				ArgumentList = { "restore", "--verbosity", "quiet", "--disable-build-servers" },
				WorkingDirectory = tempDir,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			using var proc = Process.Start(psi)!;

			// Read both pipes concurrently and put the timeout on WaitForExit.
			// A synchronous ReadToEnd() before WaitForExit() blocks until the pipe
			// closes, which would make the timeout unreachable if restore stalls.
			var stdoutTask = proc.StandardOutput.ReadToEndAsync();
			var stderrTask = proc.StandardError.ReadToEndAsync();

			if (!proc.WaitForExit(RestoreTimeoutMs))
			{
				try { proc.Kill(entireProcessTree: true); } catch { }
				return new DownloadResult(false, $"Package restore timed out after {RestoreTimeoutMs / 1000}s");
			}

			// Drain the pipes with a grace period — a child that inherited the
			// handles can keep them open past the parent's exit.
			Task.WaitAll(new Task[] { stdoutTask, stderrTask }, PipeDrainTimeoutMs);
			var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
			var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";

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
