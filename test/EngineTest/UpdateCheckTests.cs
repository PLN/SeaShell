using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SeaShell;
using SeaShell.Engine;
using Xunit;

namespace EngineTest;

public sealed class UpdateCheckTests : IDisposable
{
	private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"seashell-updatecheck-test-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try { Directory.Delete(_dataDir, recursive: true); } catch { }
	}

	// ── File format and comparison ─────────────────────────────────────

	[Fact]
	public void Write_ThenRead_GivesVersionAndTimestamp()
	{
		var at = new DateTimeOffset(2026, 11, 12, 14, 30, 0, TimeSpan.FromHours(1));
		UpdateCheck.Write(_dataDir, "0.6.3.290", at);

		var lines = File.ReadAllLines(UpdateCheck.PathIn(_dataDir));
		Assert.Equal("0.6.3.290", lines[0]);
		Assert.Equal("2026-11-12T14:30:00.0000000+01:00", lines[1]);

		var info = UpdateCheck.Read(_dataDir);
		Assert.NotNull(info);
		Assert.Equal(new Version(0, 6, 3, 290), info!.Latest);
		Assert.Equal(at, info.CheckedAt);
	}

	[Fact]
	public void Read_MissingOrGarbage_IsNull()
	{
		Assert.Null(UpdateCheck.Read(_dataDir));
		Directory.CreateDirectory(_dataDir);
		File.WriteAllText(UpdateCheck.PathIn(_dataDir), "not a version\n");
		Assert.Null(UpdateCheck.Read(_dataDir));
	}

	[Theory]
	[InlineData("0.6.3.290", "0.6.2.283", true)]
	[InlineData("0.6.3.290", "0.6.3.290", false)]
	[InlineData("0.6.2.283", "0.6.3.290", false)]
	[InlineData("0.6.3", "0.6.3.0", false)]   // missing parts count as zero
	[InlineData("0.6.3.1", "0.6.3", true)]
	[InlineData("0.6.3.290", "garbage", false)]
	public void IsNewerThan_ComparesVersions(string latest, string installed, bool expected)
	{
		var info = new UpdateCheckInfo(Version.Parse(latest), null);
		Assert.Equal(expected, info.IsNewerThan(installed));
	}

	// ── Daemon writer: NuGetUpdater.CheckSeaShellReleaseAsync ──────────

	private const string ServiceIndex = "https://feed.test/v3/index.json";

	private sealed class FakeFeed : HttpMessageHandler
	{
		public HttpStatusCode Status = HttpStatusCode.OK;
		public List<string> Requests = new();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var url = request.RequestUri!.ToString();
			Requests.Add(url);
			string? body = url switch
			{
				ServiceIndex => """{"resources":[{"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}""",
				"https://feed.test/flat/seashell/index.json" => """{"versions":["0.6.2.279","0.6.2.283","0.6.3.290","0.6.4.1-beta"]}""",
				_ => null,
			};
			var status = body == null ? HttpStatusCode.NotFound : Status;
			return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body ?? "") });
		}
	}

	private static NuGetUpdater Updater(FakeFeed feed) =>
		new(new NuGetResolver(), new HttpClient(feed),
			new List<NuGetSources.PackageSource> { new("test", ServiceIndex) });

	[Fact]
	public async Task SuccessfulLookup_WritesLatestStableVersion()
	{
		var feed = new FakeFeed();
		var before = DateTimeOffset.Now.AddSeconds(-1);

		var lookup = await Updater(feed).CheckSeaShellReleaseAsync(_dataDir);

		Assert.Equal(NuGetUpdater.LookupStatus.Found, lookup.Status);
		Assert.Equal("0.6.3.290", lookup.Version);
		var info = UpdateCheck.Read(_dataDir);
		Assert.Equal(new Version(0, 6, 3, 290), info!.Latest);
		Assert.True(info.CheckedAt >= before);
		// Metadata only: the service index and the flat-container version list, no package download.
		Assert.All(feed.Requests, u => Assert.DoesNotContain(".nupkg", u));
	}

	[Fact]
	public async Task FailedLookup_LeavesFileUntouched()
	{
		var earlier = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(1));
		UpdateCheck.Write(_dataDir, "0.6.2.283", earlier);
		var original = File.ReadAllText(UpdateCheck.PathIn(_dataDir));

		var lookup = await Updater(new FakeFeed { Status = HttpStatusCode.ServiceUnavailable }).CheckSeaShellReleaseAsync(_dataDir);

		Assert.Equal(NuGetUpdater.LookupStatus.ServiceError, lookup.Status);
		Assert.Equal(original, File.ReadAllText(UpdateCheck.PathIn(_dataDir)));
	}
}
