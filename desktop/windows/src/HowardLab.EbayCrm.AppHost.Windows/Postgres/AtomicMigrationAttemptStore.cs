using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Migrations;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Native;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public enum AtomicMigrationWriteStage
{
    BeforeRename,
}

public sealed class AtomicMigrationAttemptStore : IMigrationAttemptStore
{
    public const int MaximumMarkerBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<string> PropertyNames =
    [
        "recordVersion", "operationId", "appVersion", "startingSchemaVersion",
        "targetSchemaVersion", "state", "startedAtUtc", "finishedAtUtc", "reasonCode",
    ];

    private readonly DataProfileIdentity _profile;
    private readonly Action<AtomicMigrationWriteStage>? _writeHook;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicMigrationAttemptStore(string profileRoot)
        : this(profileRoot, null)
    {
    }

    internal AtomicMigrationAttemptStore(
        string profileRoot,
        Action<AtomicMigrationWriteStage>? writeHook)
    {
        _profile = DataProfileIdentity.Create(profileRoot);
        var runtime = Path.Combine(_profile.CanonicalPath, "runtime");
        MarkerPath = Path.Combine(runtime, "migration-attempt.json");
        TemporaryPath = MarkerPath + ".tmp";
        _writeHook = writeHook;
    }

    public string MarkerPath { get; }
    public string TemporaryPath { get; }

    public async ValueTask<MigrationAttemptRecord?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReconcileStaleTemporaryFile(cancellationToken);
            ValidatePath(MarkerPath);
            if (!File.Exists(MarkerPath)) return null;
            ValidatePath(MarkerPath);

            using var stream = new FileStream(
                MarkerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumMarkerBytes)
                throw new MigrationAttemptStoreException("migration-marker-oversize");

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return Parse(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(
        MigrationAttemptRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var bytes = Serialize(record);
        if (bytes.Length > MaximumMarkerBytes)
            throw new MigrationAttemptStoreException("migration-marker-oversize");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtimeDirectory = Path.GetDirectoryName(MarkerPath)!;
            ValidatePath(runtimeDirectory);
            Directory.CreateDirectory(runtimeDirectory);
            ValidatePath(runtimeDirectory);
            ReconcileStaleTemporaryFile(cancellationToken);
            ValidatePath(MarkerPath);
            ValidatePath(TemporaryPath);

            try
            {
                WriteTemporaryFile(bytes, cancellationToken);
                ValidatePath(TemporaryPath);
                ValidatePath(MarkerPath);
                _writeHook?.Invoke(AtomicMigrationWriteStage.BeforeRename);
                if (!PostgresNativeMethods.MoveFileEx(
                    TemporaryPath,
                    MarkerPath,
                    PostgresNativeMethods.MoveFileReplaceExisting | PostgresNativeMethods.MoveFileWriteThrough))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                TryDeleteTemporaryFile();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private unsafe void WriteTemporaryFile(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var descriptor = NativeSecurityDescriptor.CreateForCurrentUserOnly();
        var attributes = new PostgresNativeMethods.SecurityAttributes
        {
            Length = checked((uint)sizeof(PostgresNativeMethods.SecurityAttributes)),
            SecurityDescriptor = (void*)descriptor.DangerousGetHandle(),
            InheritHandle = 0,
        };
        using var handle = PostgresNativeMethods.CreateFile(
            TemporaryPath,
            PostgresNativeMethods.GenericWrite,
            shareMode: 0,
            &attributes,
            PostgresNativeMethods.CreateNew,
            PostgresNativeMethods.FileAttributeNormal | PostgresNativeMethods.FileFlagWriteThrough,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

        using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] Serialize(MigrationAttemptRecord record)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("recordVersion", record.RecordVersion);
            writer.WriteString("operationId", record.OperationId);
            writer.WriteString("appVersion", record.AppVersion.ToString());
            writer.WriteNumber("startingSchemaVersion", record.StartingSchemaVersion);
            writer.WriteNumber("targetSchemaVersion", record.TargetSchemaVersion);
            writer.WriteString("state", record.State.ToString());
            writer.WriteString("startedAtUtc", record.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            if (record.FinishedAtUtc is { } finished)
                writer.WriteString("finishedAtUtc", finished.ToString("O", CultureInfo.InvariantCulture));
            else
                writer.WriteNull("finishedAtUtc");
            writer.WriteString("reasonCode", record.ReasonCode);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static MigrationAttemptRecord Parse(byte[] bytes)
    {
        try
        {
            var json = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MigrationAttemptStoreException("migration-marker-invalid");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new MigrationAttemptStoreException("migration-marker-duplicate-property");
                if (!PropertyNames.Contains(property.Name))
                    throw new MigrationAttemptStoreException("migration-marker-unknown-property");
            }
            if (!PropertyNames.SetEquals(seen))
                throw new MigrationAttemptStoreException("migration-marker-invalid");

            var recordVersion = root.GetProperty("recordVersion").GetInt32();
            if (recordVersion != MigrationAttemptRecord.CurrentRecordVersion)
                throw new MigrationAttemptStoreException("migration-marker-version-unsupported");
            var operationId = root.GetProperty("operationId").GetGuid();
            var versionText = root.GetProperty("appVersion").GetString();
            if (!Version.TryParse(versionText, out var appVersion) ||
                appVersion.Build < 0 ||
                !string.Equals(versionText, appVersion.ToString(), StringComparison.Ordinal))
            {
                throw new MigrationAttemptStoreException("migration-marker-invalid");
            }
            var stateText = root.GetProperty("state").GetString();
            if (!Enum.TryParse<MigrationAttemptState>(stateText, ignoreCase: false, out var state) ||
                !Enum.IsDefined(state) ||
                !string.Equals(stateText, state.ToString(), StringComparison.Ordinal))
            {
                throw new MigrationAttemptStoreException("migration-marker-invalid");
            }

            var started = ParseTimestamp(root.GetProperty("startedAtUtc"));
            var finishedElement = root.GetProperty("finishedAtUtc");
            var finished = finishedElement.ValueKind == JsonValueKind.Null
                ? (DateTimeOffset?)null
                : ParseTimestamp(finishedElement);
            return new MigrationAttemptRecord(
                operationId,
                appVersion,
                root.GetProperty("startingSchemaVersion").GetInt32(),
                root.GetProperty("targetSchemaVersion").GetInt32(),
                state,
                started,
                finished,
                root.GetProperty("reasonCode").GetString()!);
        }
        catch (MigrationAttemptStoreException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new MigrationAttemptStoreException("migration-marker-invalid", error);
        }
    }

    private static DateTimeOffset ParseTimestamp(JsonElement element)
    {
        var text = element.GetString();
        if (!DateTimeOffset.TryParseExact(
            text,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value) ||
            value.Offset != TimeSpan.Zero)
        {
            throw new MigrationAttemptStoreException("migration-marker-invalid");
        }
        return value;
    }

    private void ReconcileStaleTemporaryFile(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(TemporaryPath);
        TryDeleteTemporaryFile();
    }

    private void TryDeleteTemporaryFile()
    {
        if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath);
    }

    private static void ValidatePath(string path) =>
        DataProfileIdentity.EnsureNoReparsePoints(
            Path.GetFullPath(path),
            WindowsProfilePathInspector.Instance);
}

public sealed class MigrationAttemptStoreException : Exception
{
    public MigrationAttemptStoreException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

internal static unsafe partial class PostgresNativeMethods
{
    internal const uint FileFlagWriteThrough = 0x80000000;
    internal const uint MoveFileReplaceExisting = 0x00000001;
    internal const uint MoveFileWriteThrough = 0x00000008;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
