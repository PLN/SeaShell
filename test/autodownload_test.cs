//sea_nuget Humanizer.Core
using Humanizer;

Console.WriteLine("Auto-download test:");
Console.WriteLine($"  1234567 bytes = {1234567.Bytes().Humanize()}");
Console.WriteLine($"  3 days ago = {DateTime.UtcNow.AddDays(-3).Humanize()}");
Console.WriteLine($"  'some_variable' = {"some_variable".Humanize()}");
return 0;
