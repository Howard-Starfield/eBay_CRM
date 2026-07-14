namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public sealed class SecretValue
{
    private readonly string _value;

    public SecretValue(string value)
    {
        _value = SecretCanary.Validate(value, nameof(value));
    }

    public string RevealForChildEnvironment() => _value;

    public override string ToString() => SecretCanary.RedactedText;
}

internal static class SecretCanary
{
    internal const string RedactedText = "[REDACTED]";

    internal static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (RedactedText.Contains(value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Secret canaries cannot be contained in the diagnostic redaction token.",
                parameterName);
        }

        return value;
    }
}
