'sea_nuget Humanizer.Core

Imports Humanizer

Module Program
	Sub Main(args As String())
		Console.WriteLine("Hello from SeaShell VB!")
		Console.WriteLine("some_variable_name".Humanize())
		Console.WriteLine($"Sea.StartDir = {Sea.StartDir}")
	End Sub
End Module
