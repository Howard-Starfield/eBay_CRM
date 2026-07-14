using HowardLab.EbayCrm.AppHost.Core;

namespace HowardLab.EbayCrm.AppHost.Core.Tests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void CoreAssemblyDeclaresTheAppHostIdentity()
    {
        Assert.Equal("HowardLab eBay CRM AppHost", AppHostAssembly.Name);
        Assert.Equal(10, Environment.Version.Major);
    }
}
