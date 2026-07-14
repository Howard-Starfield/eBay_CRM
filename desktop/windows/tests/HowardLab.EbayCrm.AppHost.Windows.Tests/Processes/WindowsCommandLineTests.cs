using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Processes;

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\path with space\\", "\"C:\\path with space\\\\\"")]
    [InlineData("雪だるま", "雪だるま")]
    public void QuoteArgument_UsesWindowsCrtRules(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(argument));
    }

    [Fact]
    public void QuoteArgument_RejectsEmbeddedNull()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.QuoteArgument("before\0after"));
    }
}
