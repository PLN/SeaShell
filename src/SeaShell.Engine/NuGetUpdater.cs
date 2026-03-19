using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeaShell.Engine;

/// <summary>
/// Scans the user's NuGet cache and checks each package for available updates.
/// Downloads new versions into the cache so the next script run picks them up
/// automatically (the resolver always picks the latest cached version).
///
/// Designed to run on a background timer — 8 hours for the daemon, or whatever
/// the Host's caller wants.
/// </summary>
public sealed class NuGetUpdater
{
	private readonly string _cacheRoot;
	private readonly HttpClient _http;
	private readonly NuGetResolver _resolver;
	private readonly NuGetSources _sources;

	/// <summary>Fires for each package checked. For logging/progress.</summary>
	public event Action<string>? Log;

	public NuGetUpdater(NuGetResolver resolver)
	{
		_cacheRoot = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".nuget", "packages");
		_http = new HttpClient();
		_http.DefaultRequestHeaders.Add("User-Agent", "SeaShell/1.0");
		_resolver = resolver;
		_sources = new NuGetSources(_http);
	}

	public sealed record UpdateResult
	{
		public int Checked { get; init; }
		public int Updated { get; init; }
		public int Failed { get; init; }
		public List<string> UpdatedPackages { get; init; } = new();
		public List<string> Errors { get; init; } = new();
	}

	/// <summary>
	/// Check all packages in the NuGet cache for updates. Downloads new versions
	/// into the cache. Thread-safe — safe to call from a background timer.
	/// </summary>
	public async Task<UpdateResult> CheckForUpdatesAsync(CancellationToken ct = default)
	{
		if (!Directory.Exists(_cacheRoot))
			return new UpdateResult();

		// Circuit breaker: probe configured sources before iterating hundreds of packages.
		// If no source is reachable (corporate firewall, offline, etc.), bail immediately.
		var reachableSource = await _sources.ProbeAsync(ct);
		if (reachableSource == null)
		{
			Log?.Invoke("No NuGet sources reachable — skipping update check");
			return new UpdateResult();
		}
		Log?.Invoke($"NuGet sources: {string.Join(", ", _sources.GetSources().Select(s => s.Name))}");

		var packageDirs = Directory.GetDirectories(_cacheRoot);
		var updated = new List<string>();
		var errors = new List<string>();
		int checked_ = 0;
		int consecutiveFailures = 0;

		foreach (var packageDir in packageDirs)
		{
			ct.ThrowIfCancellationRequested();

			// Mid-run circuit breaker: if we get 5 consecutive network failures,
			// the connection probably dropped — stop hammering.
			if (consecutiveFailures >= 5)
			{
				Log?.Invoke("Too many consecutive failures — aborting update check");
				break;
			}

			var packageName = Path.GetFileName(packageDir);

			// Skip SDK-managed platform packages — can't be restored via normal projects
			// and would hang or error on dotnet restore.
			if (IsSdkManagedPackage(packageName)) continue;

			checked_++;

			try
			{
				var cachedVersion = GetLatestCachedVersion(packageDir);
				if (cachedVersion == null) continue;

				var lookup = await GetLatestVersionAsync(packageName, ct);

				if (lookup.Status == LookupStatus.ServiceError)
				{
					consecutiveFailures++;
					continue;
				}
				consecutiveFailures = 0; // 404 and success both mean the API is alive

				if (lookup.Version == null) continue; // not found or no stable version

				if (IsNewer(lookup.Version, cachedVersion))
				{
					Log?.Invoke($"Updating {packageName}: {cachedVersion} → {lookup.Version}");

					var result = NuGetDownloader.Download(packageName, lookup.Version);
					if (result.Success)
					{
						updated.Add($"{packageName} {cachedVersion} → {lookup.Version}");
						_resolver.InvalidateCache(packageName);
					}
					else
					{
						errors.Add($"{packageName}: {result.Error}");
					}
				}
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				errors.Add($"{packageName}: {ex.Message}");
			}
		}

		if (updated.Count > 0)
			_resolver.InvalidateCache();

		return new UpdateResult
		{
			Checked = checked_,
			Updated = updated.Count,
			Failed = errors.Count,
			UpdatedPackages = updated,
			Errors = errors,
		};
	}

	/// <summary>Get the latest version directory in the local cache for a package.</summary>
	private static string? GetLatestCachedVersion(string packageDir)
	{
		var versions = Directory.GetDirectories(packageDir)
			.Select(Path.GetFileName)
			.Where(v => v != null && char.IsDigit(v[0]))
			.OrderByDescending(v => v)
			.FirstOrDefault();
		return versions;
	}

	private enum LookupStatus { Found, NotFound, ServiceError }
	private sealed record VersionLookup(LookupStatus Status, string? Version);

	/// <summary>Query all configured NuGet sources for the latest stable version.</summary>
	private async Task<VersionLookup> GetLatestVersionAsync(string packageName, CancellationToken ct)
	{
		string? bestVersion = null;
		bool anyServiceError = false;

		foreach (var source in _sources.GetSources())
		{
			var result = await QuerySourceAsync(source, packageName, ct);

			if (result.Status == LookupStatus.ServiceError)
			{
				anyServiceError = true;
				continue;
			}

			if (result.Version != null)
			{
				// Keep the highest version across all sources
				if (bestVersion == null || IsNewer(result.Version, bestVersion))
					bestVersion = result.Version;
			}
		}

		if (bestVersion != null)
			return new(LookupStatus.Found, bestVersion);

		// If all sources had service errors, that's a failure (circuit breaker should count it).
		// If some returned 404 and others had errors, treat as not-found (package probably just
		// doesn't exist, and the erroring source was incidental).
		return anyServiceError && bestVersion == null
			? new(LookupStatus.ServiceError, null)
			: new(LookupStatus.NotFound, null);
	}

	private async Task<VersionLookup> QuerySourceAsync(
		NuGetSources.PackageSource source, string packageName, CancellationToken ct)
	{
		try
		{
			var baseUrl = await _sources.GetFlatContainerUrlAsync(source, ct);
			if (baseUrl == null) return new(LookupStatus.ServiceError, null);

			var url = $"{baseUrl}{packageName.ToLowerInvariant()}/index.json";
			var response = await _http.GetAsync(url, ct);

			if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
				return new(LookupStatus.NotFound, null);

			if (!response.IsSuccessStatusCode)
				return new(LookupStatus.ServiceError, null);

			var json = await response.Content.ReadAsStringAsync(ct);
			var doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("versions", out var versions))
				return new(LookupStatus.NotFound, null);

			string? latest = null;
			foreach (var v in versions.EnumerateArray())
			{
				var ver = v.GetString();
				if (ver != null && !ver.Contains('-'))
					latest = ver;
			}

			return new(latest != null ? LookupStatus.Found : LookupStatus.NotFound, latest);
		}
		catch (OperationCanceledException) { throw; }
		catch
		{
			return new(LookupStatus.ServiceError, null);
		}
	}

	/// <summary>Simple semver comparison: is 'available' newer than 'cached'?</summary>
	private static bool IsNewer(string available, string cached)
	{
		if (available == cached) return false;

		var aParts = available.Split('.').Select(ParseVersionPart).ToArray();
		var cParts = cached.Split('.').Select(ParseVersionPart).ToArray();

		var len = Math.Max(aParts.Length, cParts.Length);
		for (int i = 0; i < len; i++)
		{
			var a = i < aParts.Length ? aParts[i] : 0;
			var c = i < cParts.Length ? cParts[i] : 0;
			if (a > c) return true;
			if (a < c) return false;
		}

		return false;
	}

	private static int ParseVersionPart(string s)
	{
		int.TryParse(s, out var n);
		return n;
	}

	/// <summary>
	/// SDK-managed packages that can't be restored via a normal project.
	/// These are runtime packs, targeting packs, and host bundles managed by
	/// `dotnet install` and the SDK itself.
	/// </summary>
	private static bool IsSdkManagedPackage(string name) =>
		name.Contains(".app.runtime.", StringComparison.OrdinalIgnoreCase) ||
		name.Contains(".app.host.", StringComparison.OrdinalIgnoreCase) ||
		name.Contains(".app.ref", StringComparison.OrdinalIgnoreCase) ||
		name.Contains(".app.internal", StringComparison.OrdinalIgnoreCase) ||
		name.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase);
}
