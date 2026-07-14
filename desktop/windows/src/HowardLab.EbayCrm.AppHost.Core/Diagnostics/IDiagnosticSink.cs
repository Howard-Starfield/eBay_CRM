namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public interface IDiagnosticSink : IAsyncDisposable
{
    ValueTask WriteAsync(
        DiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default);
}
