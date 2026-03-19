// The thing that CS-Script can't do: classes and top-level code in any order.

Console.WriteLine("Before class definition");

var helper = new MyHelper("SeaShell");
Console.WriteLine(helper.Greet());

class MyHelper
{
	private readonly string _name;
	public MyHelper(string name) => _name = name;
	public string Greet() => $"Hello from {_name}!";
}

// More top-level code AFTER the class — this is what breaks in CS-Script
Console.WriteLine("After class definition");
Console.WriteLine($"Double of 21 = {MyHelper2.Double(21)}");

record MyHelper2
{
	public static int Double(int x) => x * 2;
}

Console.WriteLine("All done!");
return 0;
