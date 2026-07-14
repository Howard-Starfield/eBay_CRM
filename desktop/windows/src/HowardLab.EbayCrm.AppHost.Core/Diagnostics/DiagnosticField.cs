namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public enum DiagnosticFieldKind
{
    String,
    Integer,
    Boolean,
    Guid,
    Timestamp,
    ReasonCode,
}

public sealed class DiagnosticField
{
    private DiagnosticField(
        DiagnosticFieldKind kind,
        string? text = null,
        long integer = default,
        bool boolean = default,
        System.Guid guid = default,
        DateTimeOffset timestamp = default)
    {
        Kind = kind;
        Text = text;
        IntegerValue = integer;
        BooleanValue = boolean;
        GuidValue = guid;
        TimestampValue = timestamp;
    }

    public DiagnosticFieldKind Kind { get; }

    internal string? Text { get; }

    internal long IntegerValue { get; }

    internal bool BooleanValue { get; }

    internal System.Guid GuidValue { get; }

    internal DateTimeOffset TimestampValue { get; }

    public static DiagnosticField String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DiagnosticField(DiagnosticFieldKind.String, text: value);
    }

    public static DiagnosticField Integer(long value) =>
        new(DiagnosticFieldKind.Integer, integer: value);

    public static DiagnosticField Boolean(bool value) =>
        new(DiagnosticFieldKind.Boolean, boolean: value);

    public static DiagnosticField Guid(System.Guid value) =>
        new(DiagnosticFieldKind.Guid, guid: value);

    public static DiagnosticField Timestamp(DateTimeOffset value) =>
        new(DiagnosticFieldKind.Timestamp, timestamp: value);

    public static DiagnosticField ReasonCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || !IsReasonCodeStart(value[0]) || value.Any(character => !IsReasonCodeCharacter(character)))
        {
            throw new ArgumentException(
                "Reason codes must contain only lowercase ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(value));
        }

        return new DiagnosticField(DiagnosticFieldKind.ReasonCode, text: value);
    }

    internal DiagnosticField SanitizeText(Func<string, string> sanitizer)
    {
        return Kind is DiagnosticFieldKind.String or DiagnosticFieldKind.ReasonCode
            ? new DiagnosticField(Kind, text: sanitizer(Text!))
            : this;
    }

    private static bool IsReasonCodeStart(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsReasonCodeCharacter(char character) =>
        IsReasonCodeStart(character) || character is '.' or '_' or '-';
}
