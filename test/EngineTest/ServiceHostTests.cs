using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SeaShell.Invoker;
using SeaShell.ServiceHost;
using SeaShell.ServiceHost.Installers;
using Xunit;

namespace SeaShell.Engine.Tests;

public class ServiceHostTests
{
	private static CompiledScript Compiled(string cacheEntry) => new(
		Path.Combine(Path.GetTempPath(), cacheEntry, "svc.dll"), null, null,
		Path.Combine(Path.GetTempPath(), cacheEntry, "svc.sea.json"), null,
		DirectExe: false, Elevated: false, Watch: false, Restart: false);

	private static (PackageUpdateReload Reload, List<string> Requests) Monitor(CompiledScript running, CompiledScript latest)
	{
		var requests = new List<string>();
		var reload = new PackageUpdateReload(
			_ => Task.FromResult<CompiledScript?>(latest),
			() => running,
			reason => { requests.Add(reason); return true; },
			NullLogger.Instance);
		return (reload, requests);
	}

	[Fact]
	public async Task ReloadOnPackageUpdate_SameCacheEntry_DoesNotReload()
	{
		var (reload, requests) = Monitor(Compiled("svc_aaaaaaaa"), Compiled("svc_aaaaaaaa"));

		Assert.False(await reload.CheckAsync(CancellationToken.None));
		Assert.Empty(requests);
	}

	[Fact]
	public async Task ReloadOnPackageUpdate_NewCacheEntry_Reloads()
	{
		// A newer version of a versionless //sea_nuget package changes the compile hash
		var (reload, requests) = Monitor(Compiled("svc_aaaaaaaa"), Compiled("svc_bbbbbbbb"));

		Assert.True(await reload.CheckAsync(CancellationToken.None));
		Assert.Equal(new[] { "package update" }, requests);
	}

	[Fact]
	public void WindowsStart_AlreadyRunning_IsSuccess()
	{
		Assert.Equal(0, WindowsServiceInstaller.StartExitCode(WindowsServiceInstaller.ErrorServiceAlreadyRunning));
		Assert.Equal(1060, WindowsServiceInstaller.StartExitCode(1060)); // not installed stays a failure
	}
}
