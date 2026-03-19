using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SeaShell.Engine;

/// <summary>
/// Reads NuGet package sources from the standard nuget.config files
/// and discovers their V3 flat container (PackageBaseAddress) endpoints.
///
/// Sources are read from (in order, merged):
///   1. Machine-level config
///   2. User-level config (%APPDATA%\NuGet\NuGet.Config or ~/.nuget/NuGet/NuGet.Config)
///   3. Any nuget.config files in parent directories (solution-level)
///
/// For SeaShell, we only read user + machine level — no solution context.
/// </summary>
public sealed class NuGetSources
{
	private readonly HttpClient _http;
	private readonly Dictionary<string, string> _flatContainerCache = new(StringComparer.OrdinalIgnoreCase);
	private List<PackageSource>? _sources;

	public sealed record PackageSource(string Name, string ServiceIndexUrl);

	public NuGetSources(HttpClient http)
	{
		_http = http;
	}

	/// <summary>Get all configured package sources.</summary>
	public List<PackageSource> GetSources()
	{
		if (_sources != null) return _sources;

		_sources = new List<PackageSource>();

		var configPath = GetUserConfigPath();
		if (configPath != null && File.Exists(configPath))
		{
			try
			{
				var doc = XDocument.Load(configPath);
				var packageSources = doc.Root?.Element("packageSources");
				if (packageSources != null)
				{
					foreach (var el in packageSources.Elements("add"))
					{
						var key = el.Attribute("key")?.Value;
						var value = el.Attribute("value")?.Value;
						if (key != null && value != null)
							_sources.Add(new PackageSource(key, value));
					}
				}
			}
			catch { }
		}

		// Ensure nuget.org is always present as fallback
		if (!_sources.Any(s => s.ServiceIndexUrl.Contains("nuget.org", StringComparison.OrdinalIgnoreCase)))
			_sources.Add(new PackageSource("nuget.org", "https://api.nuget.org/v3/index.json"));

		return _sources;
	}

	/// <summary>
	/// Get the flat container (PackageBaseAddress) URL for a source.
	/// This is the endpoint used for version lookups: {baseUrl}/{id}/index.json
	/// </summary>
	public async Task<string?> GetFlatContainerUrlAsync(PackageSource source, CancellationToken ct)
	{
		if (_flatContainerCache.TryGetValue(source.ServiceIndexUrl, out var cached))
			return cached;

		try
		{
			var resp = await _http.GetAsync(source.ServiceIndexUrl, ct);
			if (!resp.IsSuccessStatusCode) return null;

			var json = await resp.Content.ReadAsStringAsync(ct);
			var doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("resources", out var resources))
				return null;

			foreach (var resource in resources.EnumerateArray())
			{
				var type = resource.GetProperty("@type").GetString();
				if (type != null && type.StartsWith("PackageBaseAddress", StringComparison.OrdinalIgnoreCase))
				{
					var url = resource.GetProperty("@id").GetString();
					if (url != null)
					{
						// Normalize: ensure trailing slash
						if (!url.EndsWith('/')) url += '/';
						_flatContainerCache[source.ServiceIndexUrl] = url;
						return url;
					}
				}
			}
		}
		catch { }

		return null;
	}

	/// <summary>
	/// Probe all configured sources to check if any are reachable.
	/// Returns the first reachable source, or null if none respond.
	/// </summary>
	public async Task<PackageSource?> ProbeAsync(CancellationToken ct)
	{
		foreach (var source in GetSources())
		{
			try
			{
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				cts.CancelAfter(10_000);
				var url = await GetFlatContainerUrlAsync(source, cts.Token);
				if (url != null) return source;
			}
			catch { }
		}
		return null;
	}

	private static string? GetUserConfigPath()
	{
		// Windows: %APPDATA%\NuGet\NuGet.Config
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (!string.IsNullOrEmpty(appData))
		{
			var path = Path.Combine(appData, "NuGet", "NuGet.Config");
			if (File.Exists(path)) return path;
		}

		// Linux/macOS: ~/.nuget/NuGet/NuGet.Config
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (!string.IsNullOrEmpty(home))
		{
			var path = Path.Combine(home, ".nuget", "NuGet", "NuGet.Config");
			if (File.Exists(path)) return path;
		}

		return null;
	}
}
