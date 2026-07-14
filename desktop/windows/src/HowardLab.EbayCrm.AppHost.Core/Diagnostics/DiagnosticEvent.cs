namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public sealed class DiagnosticEvent
{
    private const int MaximumFieldCount = 64;
    private readonly KeyValuePair<string, DiagnosticField>[] _fields;

    private DiagnosticEvent(string name, KeyValuePair<string, DiagnosticField>[] fields)
    {
        Name = name;
        _fields = fields;
    }

    public string Name { get; }

    internal IReadOnlyList<KeyValuePair<string, DiagnosticField>> Fields => _fields;

    public static DiagnosticEvent Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new DiagnosticEvent(name, []);
    }

    public DiagnosticEvent With(string name, DiagnosticField value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (_fields.Length >= MaximumFieldCount)
        {
            return this;
        }

        var fields = new KeyValuePair<string, DiagnosticField>[_fields.Length + 1];
        _fields.CopyTo(fields, 0);
        fields[^1] = new KeyValuePair<string, DiagnosticField>(name, value);
        return new DiagnosticEvent(Name, fields);
    }

    internal DiagnosticEvent SanitizeText(Func<string, string> sanitizer)
    {
        var fields = new KeyValuePair<string, DiagnosticField>[_fields.Length];
        for (var index = 0; index < _fields.Length; index++)
        {
            fields[index] = new KeyValuePair<string, DiagnosticField>(
                sanitizer(_fields[index].Key),
                _fields[index].Value.SanitizeText(sanitizer));
        }

        return new DiagnosticEvent(sanitizer(Name), fields);
    }
}
