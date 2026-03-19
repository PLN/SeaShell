using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SeaShell.Engine;

/// <summary>
/// Watches a set of source files for changes with debouncing.
/// Fires Changed event after a debounce period (editor saves can cause multiple events).
/// </summary>
public sealed class ScriptWatcher : IDisposable
{
	private readonly List<FileSystemWatcher> _watchers = new();
	private readonly Timer _debounce;
	private readonly int _debounceMs;

	/// <summary>Fires after debounce period when any watched file changes.</summary>
	public event Action<string>? Changed;

	public ScriptWatcher(IEnumerable<string> filePaths, int debounceMs = 300)
	{
		_debounceMs = debounceMs;
		_debounce = new Timer(OnDebounceElapsed);

		var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var filePath in filePaths)
		{
			var dir = Path.GetDirectoryName(filePath);
			var file = Path.GetFileName(filePath);
			if (dir == null || file == null) continue;
			if (!Directory.Exists(dir)) continue;

			// One watcher per unique directory
			if (!dirs.Add(dir + "|" + file))
				continue;

			var watcher = new FileSystemWatcher(dir, file)
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
				EnableRaisingEvents = true,
			};
			// Watch all mutation types — editors may delete+create (sed -i, vim),
			// rename-over (VS Code, Notepad++), or modify in-place.
			watcher.Changed += OnFileChanged;
			watcher.Created += OnFileChanged;
			watcher.Renamed += (s, e) => OnFileChanged(s, e);
			_watchers.Add(watcher);
		}
	}

	private string? _lastChangedFile;

	private void OnFileChanged(object sender, FileSystemEventArgs e)
	{
		_lastChangedFile = e.FullPath;
		_debounce.Change(_debounceMs, Timeout.Infinite);
	}

	private void OnDebounceElapsed(object? state)
	{
		var file = _lastChangedFile;
		if (file != null)
			Changed?.Invoke(file);
	}

	public void Dispose()
	{
		_debounce.Dispose();
		foreach (var w in _watchers)
		{
			w.EnableRaisingEvents = false;
			w.Dispose();
		}
	}
}
