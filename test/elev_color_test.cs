//sea_elevate
Console.ForegroundColor = ConsoleColor.Green;
Console.Write("GREEN ");
Console.ForegroundColor = ConsoleColor.Red;
Console.Write("RED ");
Console.ResetColor();
Console.WriteLine("RESET");
Console.WriteLine($"BufferWidth={Console.BufferWidth}");
Console.WriteLine($"WindowWidth={Console.WindowWidth}");
return 0;
