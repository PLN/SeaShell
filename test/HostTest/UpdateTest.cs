using SeaShell.Host;

class UpdateTest
{
	public static async Task Run()
	{
		var host = new ScriptHost();
		var updater = host.CreateUpdater();
		updater.Log += msg => Console.WriteLine($"  {msg}");

		Console.WriteLine("Checking NuGet packages for updates...");
		var result = await updater.CheckForUpdatesAsync();

		Console.WriteLine($"\nResult: {result.Checked} checked, {result.Updated} updated, {result.Failed} failed");
		foreach (var pkg in result.UpdatedPackages)
			Console.WriteLine($"  + {pkg}");
		foreach (var err in result.Errors.Take(3))
			Console.WriteLine($"  ! {err}");
	}
}
