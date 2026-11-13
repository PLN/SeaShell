using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class SelfCommandTests
{
	[Theory]
	[InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
	[InlineData("/usr/share/dotnet/dotnet")]
	public void UnderTheDotnetHost_RelaunchesTheEntryAssembly(string host)
	{
		// dnx / `dotnet seashell.dll`: the process path alone would start a bare dotnet.
		if (host.Contains('\\') && !System.OperatingSystem.IsWindows()) return;   // a Windows path means nothing to Linux's Path
		var command = SelfCommand.Build(host, "/home/u/.nuget/packages/seashell/0.6.3.300/tools/net10.0/any/seashell.dll");

		Assert.NotNull(command);
		Assert.Equal(host, command!.FileName);
		Assert.Equal(new[] { "/home/u/.nuget/packages/seashell/0.6.3.300/tools/net10.0/any/seashell.dll" }, command.PrefixArguments);
	}

	[Fact]
	public void FromAnApphost_RelaunchesItAsIs()
	{
		var command = SelfCommand.Build(@"C:\Users\u\.dotnet\tools\seashell.exe", @"C:\Users\u\.dotnet\tools\.store\seashell\0.6.3.300\seashell.dll");

		Assert.Equal(@"C:\Users\u\.dotnet\tools\seashell.exe", command!.FileName);
		Assert.Empty(command.PrefixArguments);
	}

	[Fact]
	public void DotnetHostWithoutEntryAssembly_CannotBeRebuilt()
	{
		Assert.Null(SelfCommand.Build(System.OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet\dotnet.exe" : "/usr/share/dotnet/dotnet", null));
		Assert.Null(SelfCommand.Build(null, "seashell.dll"));
	}
}
