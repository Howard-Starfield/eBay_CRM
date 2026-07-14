using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Processes;

public interface IControlChannel : IAsyncDisposable
{
    Task<ControlEnvelope> ReadAsync(CancellationToken cancellationToken = default);

    Task SendAsync(ControlEnvelope envelope, CancellationToken cancellationToken = default);
}
