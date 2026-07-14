using HowardLab.EbayCrm.AppHost.Core.Diagnostics;

namespace HowardLab.EbayCrm.AppHost.Core.Processes;

public interface ISupervisedProcess : IAsyncDisposable
{
    SupervisedProcessIdentity Identity { get; }

    Task<int> Completion { get; }

    BoundedTextCollector StandardOutput { get; }

    BoundedTextCollector StandardError { get; }
}
