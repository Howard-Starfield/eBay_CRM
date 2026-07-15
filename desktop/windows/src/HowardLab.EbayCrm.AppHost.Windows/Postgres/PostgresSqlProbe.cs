namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public sealed record PostgresSqlProbe(
    string SelectOne,
    string ReportedDataDirectory,
    Guid? ClusterId = null,
    int? SchemaVersion = null);

public sealed class PostgresProbeException : Exception
{
    public PostgresProbeException(string reasonCode) : base(reasonCode) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
