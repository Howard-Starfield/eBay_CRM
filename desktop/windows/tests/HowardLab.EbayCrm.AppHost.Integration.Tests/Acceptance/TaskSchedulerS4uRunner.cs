using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

internal sealed record S4uRunRequest(
    string BrokerPath,
    string AppHostPath,
    string WorkingDirectory,
    string ProfileRoot,
    string PostgresBin,
    string FixturePath,
    int Port,
    string ExpectedUserSid,
    int OwnerSessionId);

internal sealed record S4uBrokerResult(
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

internal static class TaskSchedulerS4uRunner
{
    private const int SchemaVersion = 1;
    private const int MaximumJsonBytes = 64 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(40);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static Task<S4uBrokerResult> RunAsync(
        S4uRunRequest request,
        CancellationToken cancellationToken) =>
        RunCoreAsync(
            request,
            Deadline,
            RealS4uTaskSession.Create,
            CreateRunnerRootPath,
            cancellationToken);

    internal static async Task<S4uBrokerResult> RunCoreAsync(
        S4uRunRequest request,
        TimeSpan deadline,
        Func<string, string, string, IS4uTaskSession> sessionFactory,
        Func<string> runnerRootPathFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ValidateInput(request);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var root = runnerRootPathFactory();
        var requestPath = Path.Combine(root, "request.json");
        var resultPath = Path.Combine(root, "result.json");
        var taskName = "eBayCRM-Acceptance-" + Guid.NewGuid().ToString("N");
        IS4uTaskSession? session = null;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        try
        {
            CreateCurrentUserOnlyDirectory(root);
            await WriteRequestAsync(request, requestPath, resultPath, nonce, cancellationToken);
            try
            {
                session = sessionFactory(taskName, request.BrokerPath, requestPath);
                session.Start();
            }
            catch (Exception error) when (IsS4uPolicyFailure(error.HResult))
            {
                throw new S4uEnvironmentUnavailableException("s4u-policy-unavailable");
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = session.Observe();
                if (File.Exists(resultPath))
                {
                    return await ReadAndValidateResultAsync(
                        resultPath,
                        nonce,
                        request,
                        observation.EngineProcessId,
                        cancellationToken);
                }

                if (observation.Completed)
                    throw new S4uExecutionException("broker-crash");
                await Task.Delay(20, cancellationToken);
            }

            throw new S4uExecutionException("task-timeout");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (session is not null)
            {
                TryCleanup(session.DeleteTask, cleanupErrors);
                TryCleanup(session.DeleteFolderIfEmpty, cleanupErrors);
                TryCleanup(session.Dispose, cleanupErrors);
            }

            TryCleanup(() => DeleteIfPresent(resultPath), cleanupErrors);
            TryCleanup(() => DeleteIfPresent(requestPath), cleanupErrors);
            TryCleanup(() => DeleteMatchingTemporaryResults(root), cleanupErrors);
            TryCleanup(() => DeleteDirectoryIfPresent(root), cleanupErrors);
            if (cleanupErrors.Count > 0 && primary is null)
                throw new S4uCleanupException("task-cleanup-failed", cleanupErrors);
        }
    }

    private static string CreateRunnerRootPath() =>
        Path.Combine(Path.GetTempPath(), $"ebaycrm-s4u-{Guid.NewGuid():N}");

    private static void ValidateInput(S4uRunRequest request)
    {
        ValidateLocalPath(request.BrokerPath, isDirectory: false);
        ValidateLocalPath(request.AppHostPath, isDirectory: false);
        ValidateLocalPath(request.WorkingDirectory, isDirectory: true);
        ValidateLocalPath(request.ProfileRoot, isDirectory: true);
        ValidateLocalPath(request.PostgresBin, isDirectory: true);
        ValidateLocalPath(request.FixturePath, isDirectory: false);
        if (request.Port is < 1 or > 65535 || request.OwnerSessionId < 0 ||
            !new SecurityIdentifier(request.ExpectedUserSid).IsAccountSid())
            throw new ArgumentException("invalid-s4u-request");
    }

    private static void ValidateLocalPath(string path, bool isDirectory)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("path-not-local-absolute");
        var canonical = Path.GetFullPath(path);
        var root = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network ||
            (isDirectory ? !Directory.Exists(canonical) : !File.Exists(canonical)))
            throw new ArgumentException("path-not-local-absolute");
    }

    private static async Task WriteRequestAsync(
        S4uRunRequest request,
        string requestPath,
        string resultPath,
        string nonce,
        CancellationToken cancellationToken)
    {
        var payload = new BrokerRequest(
            SchemaVersion,
            nonce,
            Path.GetFullPath(request.AppHostPath),
            Path.GetFullPath(request.WorkingDirectory),
            Path.GetFullPath(request.ProfileRoot),
            Path.GetFullPath(request.PostgresBin),
            Path.GetFullPath(request.FixturePath),
            request.Port,
            resultPath);
        await using var stream = new FileStream(
            requestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<S4uBrokerResult> ReadAndValidateResultAsync(
        string resultPath,
        string nonce,
        S4uRunRequest request,
        int? observedEngineProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(resultPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length is <= 0 or > MaximumJsonBytes)
                throw new S4uResultValidationException("result-malformed");
            var bytes = await File.ReadAllBytesAsync(resultPath, cancellationToken);
            var result = JsonSerializer.Deserialize<S4uBrokerResult>(bytes, JsonOptions)
                ?? throw new S4uResultValidationException("result-malformed");
            if (result.Version != SchemaVersion)
                throw new S4uResultValidationException("result-version-mismatch");
            if (!StringComparer.Ordinal.Equals(result.Nonce, nonce))
                throw new S4uResultValidationException("result-nonce-mismatch");
            if (!StringComparer.Ordinal.Equals(result.UserSid, request.ExpectedUserSid))
                throw new S4uResultValidationException("result-sid-mismatch");
            if (result.BrokerSessionId == request.OwnerSessionId ||
                result.ContenderSessionId != result.BrokerSessionId)
                throw new S4uResultValidationException("result-session-not-isolated");
            var now = DateTimeOffset.UtcNow;
            if (result.StartedUtc < now - TimeSpan.FromMinutes(1) ||
                result.CompletedUtc < result.StartedUtc ||
                result.CompletedUtc > now + TimeSpan.FromSeconds(5) ||
                result.ContenderStartedUtc < result.StartedUtc - TimeSpan.FromSeconds(5) ||
                result.ContenderStartedUtc > result.CompletedUtc)
                throw new S4uResultValidationException("result-stale");
            if (result.BrokerProcessId <= 0 || result.ContenderProcessId <= 0 ||
                result.BrokerProcessId == result.ContenderProcessId ||
                observedEngineProcessId is { } engine && engine != result.BrokerProcessId)
                throw new S4uResultValidationException("result-process-identity-mismatch");
            return result;
        }
        catch (S4uResultValidationException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new S4uResultValidationException("result-malformed");
        }
    }

    private static void CreateCurrentUserOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new Win32Exception("current-user-sid-unavailable");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    private static bool IsS4uPolicyFailure(int hresult) => hresult is
        unchecked((int)0x80070005) or
        unchecked((int)0x8007052E) or
        unchecked((int)0x80070569) or
        unchecked((int)0x80041314) or
        unchecked((int)0x8004131F);

    private static void TryCleanup(Action action, ICollection<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteMatchingTemporaryResults(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "result.json.*.tmp")) File.Delete(path);
    }

    private static void DeleteDirectoryIfPresent(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: false);
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
}

internal interface IS4uTaskSession : IDisposable
{
    void Start();
    S4uTaskObservation Observe();
    void DeleteTask();
    void DeleteFolderIfEmpty();
}

internal readonly record struct S4uTaskObservation(bool Completed, int? EngineProcessId);

internal sealed class RealS4uTaskSession : IS4uTaskSession
{
    private const int TaskActionExec = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonS4u = 2;
    private const int TaskRunLevelLua = 0;
    private const int TaskTriggerTime = 1;
    private readonly dynamic _root;
    private readonly dynamic _folder;
    private readonly dynamic _registeredTask;
    private dynamic? _runningTask;
    private bool _deleted;

    private RealS4uTaskSession(dynamic root, dynamic folder, dynamic registeredTask)
    {
        _root = root;
        _folder = folder;
        _registeredTask = registeredTask;
    }

    internal static IS4uTaskSession Create(string taskName, string brokerPath, string requestPath)
    {
        dynamic? service = null;
        dynamic? root = null;
        dynamic? folder = null;
        dynamic? definition = null;
        dynamic? trigger = null;
        dynamic? action = null;
        dynamic? registered = null;
        var transferred = false;
        var folderCreated = false;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
                ?? throw new COMException("task-scheduler-unavailable");
            service = Activator.CreateInstance(serviceType)
                ?? throw new COMException("task-scheduler-unavailable");
            service.Connect();
            root = service.GetFolder("\\");
            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new Win32Exception("current-user-sid-unavailable");
            var taskSddl = $"D:P(A;;FA;;;SY)(A;;FA;;;{sid.Value})";
            try
            {
                folder = root.GetFolder("\\HowardLab");
            }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002))
            {
                folder = root.CreateFolder("HowardLab", taskSddl);
                folderCreated = true;
            }

            registered = CompleteAfterFolderAcquisition(
                () =>
                {
                    definition = service.NewTask(0);
                    definition.RegistrationInfo.Description =
                        "eBayCRM same-user cross-session acceptance";
                    definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                    definition.Principal.LogonType = TaskLogonS4u;
                    definition.Principal.RunLevel = TaskRunLevelLua;
                    definition.Settings.Enabled = true;
                    definition.Settings.Hidden = true;
                    definition.Settings.AllowDemandStart = true;
                    definition.Settings.StartWhenAvailable = false;
                    definition.Settings.DisallowStartIfOnBatteries = false;
                    definition.Settings.StopIfGoingOnBatteries = false;
                    definition.Settings.ExecutionTimeLimit = "PT30S";
                    definition.Settings.MultipleInstances = 2;
                    trigger = definition.Triggers.Create(TaskTriggerTime);
                    trigger.StartBoundary = DateTime.Now.AddMinutes(5)
                        .ToString("s", CultureInfo.InvariantCulture);
                    trigger.EndBoundary = DateTime.Now.AddMinutes(6)
                        .ToString("s", CultureInfo.InvariantCulture);
                    trigger.Enabled = true;
                    action = definition.Actions.Create(TaskActionExec);
                    action.Path = brokerPath;
                    action.Arguments = "--request \"" + requestPath.Replace("\"", "\"\"") + "\"";
                    action.WorkingDirectory = Path.GetDirectoryName(brokerPath)!;
                    return folder.RegisterTaskDefinition(
                    taskName,
                    definition,
                    TaskCreate,
                    WindowsIdentity.GetCurrent().Name,
                    null,
                    TaskLogonS4u,
                    taskSddl);
                },
                folderCreated,
                () =>
                {
                    ReleaseCom(folder);
                    folder = null;
                    root.DeleteFolder("\\HowardLab", 0);
                    folderCreated = false;
                });
            var session = new RealS4uTaskSession(root, folder, registered);
            transferred = true;
            return session;
        }
        finally
        {
            ReleaseCom(action);
            ReleaseCom(trigger);
            ReleaseCom(definition);
            ReleaseCom(service);
            if (!transferred)
            {
                ReleaseCom(registered);
                ReleaseCom(folder);
                ReleaseCom(root);
            }
        }
    }

    internal static object CompleteAfterFolderAcquisition(
        Func<object> complete,
        bool folderCreated,
        Action deleteCreatedFolder)
    {
        try
        {
            return complete();
        }
        catch
        {
            if (folderCreated) deleteCreatedFolder();
            throw;
        }
    }

    public void Start() => _runningTask = _registeredTask.Run(null);

    public S4uTaskObservation Observe()
    {
        int? engine = null;
        if (_runningTask is not null)
        {
            try { engine = (int)_runningTask.EnginePID; }
            catch (COMException) { }
        }

        var state = (int)_registeredTask.State;
        var completed = state == 3 && (DateTime)_registeredTask.LastRunTime > DateTime.MinValue;
        return new S4uTaskObservation(completed, engine);
    }

    public void DeleteTask()
    {
        if (_deleted) return;
        Exception? stopError = null;
        try { _registeredTask.Stop(0); }
        catch (Exception error) when (error.HResult == unchecked((int)0x8004130B)) { }
        catch (Exception error) { stopError = error; }
        try
        {
            _folder.DeleteTask((string)_registeredTask.Name, 0);
        }
        catch (Exception deleteError) when (stopError is not null)
        {
            throw new AggregateException(stopError, deleteError);
        }
        _deleted = true;
        if (stopError is not null) throw stopError;
    }

    public void DeleteFolderIfEmpty()
    {
        try { _root.DeleteFolder("\\HowardLab", 0); }
        catch (Exception error) when (error.HResult is unchecked((int)0x80070091) or unchecked((int)0x80070005))
        {
            if (error.HResult == unchecked((int)0x80070005)) throw;
        }
    }

    public void Dispose()
    {
        ReleaseCom(_runningTask);
        ReleaseCom(_registeredTask);
        ReleaseCom(_folder);
        ReleaseCom(_root);
        _runningTask = null;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }
}

internal class S4uContractException(string reasonCode) : Exception(reasonCode)
{
    internal string ReasonCode { get; } = reasonCode;
}

internal sealed class S4uEnvironmentUnavailableException(string reasonCode) :
    S4uContractException(reasonCode);

internal sealed class S4uResultValidationException(string reasonCode) :
    S4uContractException(reasonCode);

internal sealed class S4uExecutionException(string reasonCode) :
    S4uContractException(reasonCode);

internal sealed class S4uCleanupException(
    string reasonCode,
    IReadOnlyCollection<Exception> cleanupErrors) : S4uContractException(reasonCode)
{
    internal int FailureCount { get; } = cleanupErrors.Count;
}

internal sealed class PublishedAcceptanceLayout : IDisposable
{
    private PublishedAcceptanceLayout(string root, string appHostDirectory, string brokerDirectory)
    {
        Root = root;
        AppHostDirectory = appHostDirectory;
        AppHostPath = Path.Combine(appHostDirectory, "HowardLab.EbayCrm.AppHost.exe");
        FixturePath = Path.Combine(appHostDirectory, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        BrokerPath = Path.Combine(brokerDirectory, "HowardLab.EbayCrm.AppHost.AcceptanceBroker.exe");
    }

    private string Root { get; }
    internal string AppHostDirectory { get; }
    internal string AppHostPath { get; }
    internal string FixturePath { get; }
    internal string BrokerPath { get; }

    internal static async Task<PublishedAcceptanceLayout> CreateAsync()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-task4-publish-{Guid.NewGuid():N}");
        var appHost = Path.Combine(root, "apphost");
        var broker = Path.Combine(root, "broker");
        Directory.CreateDirectory(root);
        try
        {
            await PublishAsync(repository,
                Path.Combine(repository, "desktop", "windows", "src", "HowardLab.EbayCrm.AppHost", "HowardLab.EbayCrm.AppHost.csproj"),
                appHost);
            await PublishAsync(repository,
                Path.Combine(repository, "desktop", "windows", "tests", "HowardLab.EbayCrm.AppHost.AcceptanceBroker", "HowardLab.EbayCrm.AppHost.AcceptanceBroker.csproj"),
                broker);
            return new PublishedAcceptanceLayout(root, appHost, broker);
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static async Task PublishAsync(string workingDirectory, string project, string output)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "publish", project, "--no-restore", "--configuration", "Release", "--runtime", "win-x64",
            "--self-contained", "false", "--output", output, "-p:PublishSingleFile=false",
            "-p:PublishTrimmed=false", "-p:PublishAot=false",
        }) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("publish-start-failed");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        if (process.ExitCode != 0)
            throw new InvalidOperationException("publish-failed\n" + await stdout + "\n" + await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json"))) return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("repository-root-unavailable");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal enum ResultFault
{
    WrongNonce,
    Stale,
    SameSession,
    MalformedJson,
    Timeout,
    BrokerCrash,
    CleanupFailure,
}

internal sealed class S4uRunnerHarness : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _runnerRoot;
    private readonly FakeS4uTaskSession _session;
    private readonly S4uRunRequest _request;

    private S4uRunnerHarness(
        string root,
        string runnerRoot,
        FakeS4uTaskSession session,
        S4uRunRequest request)
    {
        _root = root;
        _runnerRoot = runnerRoot;
        _session = session;
        _request = request;
    }

    internal static Task<S4uRunnerHarness> CreateAsync(ResultFault fault)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-task4-harness-{Guid.NewGuid():N}");
        var profile = Path.Combine(root, "profile");
        var bin = Path.Combine(root, "postgres");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(bin);
        var broker = Path.Combine(root, "broker.exe");
        var appHost = Path.Combine(root, "apphost.exe");
        var fixture = Path.Combine(root, "fixture.exe");
        File.WriteAllBytes(broker, [0]);
        File.WriteAllBytes(appHost, [0]);
        File.WriteAllBytes(fixture, [0]);
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("current-user-sid-unavailable");
        var ownerSession = Process.GetCurrentProcess().SessionId;
        var session = new FakeS4uTaskSession(fault, sid, ownerSession);
        var runnerRoot = Path.Combine(Path.GetTempPath(), $"ebaycrm-s4u-harness-{Guid.NewGuid():N}");
        return Task.FromResult(new S4uRunnerHarness(
            root,
            runnerRoot,
            session,
            new S4uRunRequest(
                broker, appHost, root, profile, bin, fixture, 15432, sid, ownerSession)));
    }

    internal async Task<S4uBrokerResult> RunAsync()
    {
        try
        {
            return await TaskSchedulerS4uRunner.RunCoreAsync(
                _request,
                TimeSpan.FromMilliseconds(150),
                (_, _, requestPath) => _session.Attach(requestPath),
                () => _runnerRoot,
                CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    internal void AssertRunnerOwnedArtifactsClean()
    {
        Assert.False(File.Exists(Path.Combine(_runnerRoot, "request.json")));
        Assert.False(File.Exists(Path.Combine(_runnerRoot, "result.json")));
        Assert.False(File.Exists(_session.TemporaryResultPath));
        Assert.False(Directory.Exists(_runnerRoot));
    }

    internal void AssertClean()
    {
        Assert.False(Directory.Exists(_root));
        Assert.False(_session.BrokerAlive);
        Assert.False(_session.ContenderAlive);
        Assert.False(_session.TaskExists);
    }

    internal void AssertCleanupAttempted()
    {
        Assert.True(_session.DeleteTaskAttempted);
        Assert.True(_session.DeleteFolderAttempted);
        Assert.True(_session.Disposed);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeS4uTaskSession(
    ResultFault fault,
    string sid,
    int ownerSession) : IS4uTaskSession
{
    private string? _requestPath;
    internal string? TemporaryResultPath { get; private set; }
    internal bool TaskExists { get; private set; }
    internal bool BrokerAlive { get; private set; }
    internal bool ContenderAlive { get; private set; }
    internal bool DeleteTaskAttempted { get; private set; }
    internal bool DeleteFolderAttempted { get; private set; }
    internal bool Disposed { get; private set; }

    internal IS4uTaskSession Attach(string requestPath)
    {
        _requestPath = requestPath;
        TaskExists = true;
        return this;
    }

    public void Start()
    {
        BrokerAlive = true;
        var request = JsonDocument.Parse(File.ReadAllBytes(_requestPath!)).RootElement;
        var nonce = request.GetProperty("Nonce").GetString()!;
        var resultPath = request.GetProperty("ResultPath").GetString()!;
        TemporaryResultPath = resultPath + "." + nonce + ".tmp";
        File.WriteAllText(TemporaryResultPath, "incomplete-result");
        if (fault == ResultFault.Timeout) return;
        if (fault == ResultFault.BrokerCrash)
        {
            BrokerAlive = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (fault == ResultFault.MalformedJson)
        {
            File.WriteAllText(resultPath, "{");
        }
        else
        {
            var brokerSession = fault == ResultFault.SameSession ? ownerSession : ownerSession + 1;
            var started = fault == ResultFault.Stale ? now - TimeSpan.FromMinutes(2) : now;
            var result = new S4uBrokerResult(
                1,
                fault == ResultFault.WrongNonce ? new string('0', 64) : nonce,
                sid,
                111,
                brokerSession,
                222,
                brokerSession,
                started,
                2,
                "AcquiringInstance\n",
                "profile-already-owned\n",
                1,
                started,
                started + TimeSpan.FromMilliseconds(1));
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
        }

        BrokerAlive = false;
        ContenderAlive = false;
    }

    public S4uTaskObservation Observe() => new(
        fault == ResultFault.BrokerCrash,
        fault is ResultFault.Timeout or ResultFault.BrokerCrash ? null : 111);

    public void DeleteTask()
    {
        DeleteTaskAttempted = true;
        BrokerAlive = false;
        ContenderAlive = false;
        TaskExists = false;
        if (fault == ResultFault.CleanupFailure) throw new IOException("injected-cleanup-failure");
    }

    public void DeleteFolderIfEmpty() => DeleteFolderAttempted = true;

    public void Dispose() => Disposed = true;
}
