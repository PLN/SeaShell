// Writes a marker file. Used by pipeline schedule tests.
// Usage: sea schedule_marker_test.cs [marker-path]
// When no path given, writes to working directory (set by schedule feature).

var markerPath = args.Length > 0 ? args[0]
	: Path.Combine(Environment.CurrentDirectory, "schedule_marker.txt");
File.WriteAllText(markerPath, $"OK\nTime={DateTime.UtcNow:O}\nMachine={Environment.MachineName}\nUser={Environment.UserName}\n");
