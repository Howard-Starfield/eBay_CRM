using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

return await AcceptanceBroker.RunAsync(args);

internal static class AcceptanceBroker
{
    private const int SchemaVersion = 1;
    private const int MaximumJsonBytes = 64 * 1024;
    private static readonly TimeSpan ExecutionLimit = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !StringComparer.Ordinal.Equals(args[0], "--request")) return 64;

        BrokerRequest request;
        try
        {
            request = await ReadAndValidateRequestAsync(args[1]);
        }
        catch
        {
            return 65;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var brokerProcess = Process.GetCurrentProcess();
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("current-user-sid-unavailable");
        using var job = WindowsJobObject.CreateKillOnClose();
        ISupervisedProcess? contender = null;
        try
        {
            var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
            contender = await new WindowsProcessLauncher(
                NoopDiagnosticSink.Instance,
                maxOutputBytes: MaximumJsonBytes,
                maxLineBytes: MaximumJsonBytes,
                processCleanupTimeout: TimeSpan.FromSeconds(5)).LaunchAsync(
                    new LaunchSpecification(
                        RuntimeRole.Server,
                        generation,
                        request.AppHostPath,
                        [
                            "--profile-root", request.ProfileRoot,
                            "--postgres-bin", request.PostgresBin,
                            "--fixture-path", request.FixturePath,
                            "--port", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "--mode", "run",
                            "--runtime-backend", "redis",
                            "--role-target", "controlled-fixture",
                        ],
                        request.WorkingDirectory,
                        CreateEnvironment(),
                        new Dictionary<string, SecretValue>(),
                        TimeSpan.FromSeconds(2)),
                    job,
                    CancellationToken.None);

            int contenderSessionId;
            using (var process = Process.GetProcessById(contender.Identity.ProcessId))
            {
                contenderSessionId = process.SessionId;
            }

            int exitCode;
            try
            {
                exitCode = await contender.Completion.WaitAsync(ExecutionLimit);
            }
            catch (TimeoutException)
            {
                _ = job.TerminateTree(1460);
                try
                {
                    _ = await contender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    return 70;
                }

                return 70;
            }

            var accounting = job.GetAccountingSnapshot();
            var result = new BrokerResult(
                SchemaVersion,
                request.Nonce,
                sid.Value,
                brokerProcess.Id,
                brokerProcess.SessionId,
                contender.Identity.ProcessId,
                contenderSessionId,
                contender.Identity.CreationTimeUtc,
                exitCode,
                contender.StandardOutput.Snapshot(),
                contender.StandardError.Snapshot(),
                accounting.TotalProcesses,
                startedUtc,
                DateTimeOffset.UtcNow);
            await WriteAtomicResultAsync(request.ResultPath, request.Nonce, sid, result);
            return 0;
        }
        catch
        {
            return 70;
        }
        finally
        {
            if (contender is not null) await contender.DisposeAsync();
        }
    }

    private static async Task<BrokerRequest> ReadAndValidateRequestAsync(string requestPath)
    {
        ValidateLocalAbsolutePath(requestPath, mustExist: true, expectDirectory: false);
        RejectReparsePoint(requestPath);
        var info = new FileInfo(requestPath);
        if (info.Length is <= 0 or > MaximumJsonBytes) throw new InvalidDataException();
        var bytes = await File.ReadAllBytesAsync(requestPath);
        var request = JsonSerializer.Deserialize<BrokerRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException();
        if (request.Version != SchemaVersion ||
            request.Nonce.Length != 64 ||
            request.Nonce.Any(value => !Uri.IsHexDigit(value)) ||
            request.Port is < 1 or > 65535)
        {
            throw new InvalidDataException();
        }

        ValidateLocalAbsolutePath(request.AppHostPath, mustExist: true, expectDirectory: false);
        ValidateLocalAbsolutePath(request.WorkingDirectory, mustExist: true, expectDirectory: true);
        ValidateLocalAbsolutePath(request.ProfileRoot, mustExist: true, expectDirectory: true);
        ValidateLocalAbsolutePath(request.PostgresBin, mustExist: true, expectDirectory: true);
        ValidateLocalAbsolutePath(request.FixturePath, mustExist: true, expectDirectory: false);
        ValidateLocalAbsolutePath(request.ResultPath, mustExist: false, expectDirectory: false);
        var requestParent = Path.GetDirectoryName(Path.GetFullPath(requestPath));
        var resultParent = Path.GetDirectoryName(Path.GetFullPath(request.ResultPath));
        if (!StringComparer.OrdinalIgnoreCase.Equals(requestParent, resultParent) ||
            File.Exists(request.ResultPath) || Directory.Exists(request.ResultPath))
        {
            throw new InvalidDataException();
        }

        RejectReparsePoint(resultParent!);
        return request;
    }

    private static void ValidateLocalAbsolutePath(string path, bool mustExist, bool expectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains('\0'))
        {
            throw new InvalidDataException();
        }

        var canonical = Path.GetFullPath(path);
        var root = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException();
        if (!mustExist) return;
        if (expectDirectory ? !Directory.Exists(canonical) : !File.Exists(canonical))
            throw new FileNotFoundException();
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException();
    }

    private static Dictionary<string, string> CreateEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "TEMP", "TMP" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        }

        var systemRoot = environment["SystemRoot"];
        environment["PATH"] = Path.Combine(systemRoot, "System32");
        return environment;
    }

    private static async Task WriteAtomicResultAsync(
        string resultPath,
        string nonce,
        SecurityIdentifier sid,
        BrokerResult result)
    {
        var temporaryPath = resultPath + "." + nonce + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ApplyCurrentUserOnlyAcl(temporaryPath, sid);
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, resultPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void ApplyCurrentUserOnlyAcl(string path, SecurityIdentifier sid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static NoopDiagnosticSink Instance { get; } = new();
        public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record BrokerRequest(
        int Version,
        string Nonce,
        string AppHostPath,
        string WorkingDirectory,
        string ProfileRoot,
        string PostgresBin,
        string FixturePath,
        int Port,
        string ResultPath);

    private sealed record BrokerResult(
        int Version,
        string Nonce,
        string UserSid,
        int BrokerProcessId,
        int BrokerSessionId,
        int ContenderProcessId,
        int ContenderSessionId,
        DateTimeOffset ContenderStartedUtc,
        int ExitCode,
        string StandardOutput,
        string StandardError,
        uint TotalProcesses,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc);
}
