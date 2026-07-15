namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public sealed class DiagnosticSecretRegistry
{
    private readonly object _gate = new();
    private string[] _snapshot = [];

    public void Register(SecretValue secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var canary = SecretCanary.Validate(secret.RevealForChildEnvironment(), nameof(secret));
        lock (_gate)
        {
            if (_snapshot.Contains(canary, StringComparer.Ordinal))
            {
                return;
            }

            var next = new string[_snapshot.Length + 1];
            _snapshot.CopyTo(next, 0);
            next[^1] = canary;
            Array.Sort(next, static (left, right) =>
            {
                var length = right.Length.CompareTo(left.Length);
                return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
            });
            Volatile.Write(ref _snapshot, next);
        }
    }

    internal string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var snapshot = Volatile.Read(ref _snapshot);
        foreach (var canary in snapshot)
        {
            value = value.Replace(canary, SecretCanary.RedactedText, StringComparison.Ordinal);
        }

        return value;
    }
}
