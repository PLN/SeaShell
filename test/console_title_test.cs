//sea_elevate
// Console title change test — observe whether titles propagate to the actual console window.

var original = Console.Title;
Console.WriteLine($"Original title: \"{original}\"");

Console.Title = "SeaShell — Step 1";
Console.WriteLine("Set title to: \"SeaShell — Step 1\"");
Thread.Sleep(2000);

Console.Title = "SeaShell — Step 2 (working...)";
Console.WriteLine("Set title to: \"SeaShell — Step 2 (working...)\"");
Thread.Sleep(2000);

Console.Title = "SeaShell — Step 3 (done)";
Console.WriteLine("Set title to: \"SeaShell — Step 3 (done)\"");
Thread.Sleep(2000);

// Restore
Console.Title = original;
Console.WriteLine($"Restored title to: \"{original}\"");

return 0;
