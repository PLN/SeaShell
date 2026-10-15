using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class ElevationPolicyTests
{
	[Theory]
	[InlineData(false, false, true)]   // user pass: no privileges needed
	[InlineData(false, true, true)]
	[InlineData(true, true, true)]     // system pass: must be elevated
	[InlineData(true, false, false)]   // ... and is refused otherwise
	public void SystemPassRequiresElevation(bool systemMode, bool isElevatedOrRoot, bool expected)
	{
		Assert.Equal(expected, ElevationPolicy.IsSatisfied(systemMode, isElevatedOrRoot));
	}
}