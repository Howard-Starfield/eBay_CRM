using System.Text;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

public static class WindowsCommandLine
{
    public static string Build(string applicationPath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(applicationPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var commandLine = new StringBuilder(QuoteArgument(applicationPath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0'))
        {
            throw new ArgumentException("Process arguments cannot contain NUL.", nameof(argument));
        }

        if (argument.Length > 0 && !argument.Any(character => character is ' ' or '\t' or '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashCount * 2) + 1).Append('"');
                backslashCount = 0;
                continue;
            }

            quoted.Append('\\', backslashCount).Append(character);
            backslashCount = 0;
        }

        quoted.Append('\\', backslashCount * 2).Append('"');
        return quoted.ToString();
    }
}
