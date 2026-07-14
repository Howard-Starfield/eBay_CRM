namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public sealed class SecretValue
{
    private const string RedactedText = "[REDACTED]";
    private readonly string _value;

    public SecretValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public string RevealForChildEnvironment() => _value;

    public override string ToString() => RedactedText;
}
