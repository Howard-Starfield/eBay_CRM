using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Protocol;

public sealed class ControlProtocolV2GoldenTests
{
    private static readonly string GoldenPath = FindGoldenPath();

    [Fact]
    public void VersionTwoCreationTicksAreCanonicalDecimalStrings()
    {
        const string creationTicks = "638880000000000123";
        var challenge = new IdentityChallengePayload(4242, creationTicks, "challenge-01");
        var hello = new HelloPayload(
            4242,
            creationTicks,
            "nonce-01",
            "build-01",
            "http://127.0.0.1:43123/health",
            "challenge-01");

        var challengeJson = JsonSerializer.Serialize(challenge, ControlFrameCodec.SerializerOptions);
        var helloJson = JsonSerializer.Serialize(hello, ControlFrameCodec.SerializerOptions);

        Assert.Equal(2, ControlProtocolConstants.CurrentVersion);
        Assert.Contains("\"processCreationTimeUtcTicks\":\"638880000000000123\"", challengeJson, StringComparison.Ordinal);
        Assert.Contains("\"processCreationTimeUtcTicks\":\"638880000000000123\"", helloJson, StringComparison.Ordinal);
        Assert.True(ProcessCreationTimeTicks.TryParseCanonical(creationTicks, out var parsed));
        Assert.Equal(638880000000000123L, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("92233720368547758070")]
    public void RejectsNonCanonicalOrOutOfRangeCreationTicks(string value)
    {
        Assert.False(ProcessCreationTimeTicks.TryParseCanonical(value, out _));
    }

    [Fact]
    public async Task CSharpReadsAndWritesSharedGoldenFramesByteForByte()
    {
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(GoldenPath));
        var root = artifact.RootElement;
        Assert.Equal(ControlProtocolConstants.CurrentVersion, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(ControlProtocolConstants.MaxFrameBytes, root.GetProperty("maxFrameBytes").GetInt32());
        Assert.Equal(ControlProtocolConstants.MaxFramesPerSession, root.GetProperty("maxFramesPerSession").GetInt32());
        Assert.Equal(ControlProtocolConstants.MaxTextFieldChars, root.GetProperty("maxTextFieldChars").GetInt32());

        var origins = root.GetProperty("validFrames")
            .EnumerateArray()
            .Select(vector => vector.GetProperty("origin").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("csharp", origins);
        Assert.Contains("node", origins);

        foreach (var vector in root.GetProperty("validFrames").EnumerateArray())
        {
            var expectedFrame = Convert.FromBase64String(vector.GetProperty("frameBase64").GetString()!);
            var decoded = await new ControlFrameCodec().ReadAsync(new MemoryStream(expectedFrame));
            var expectedEnvelope = vector.GetProperty("envelope").Deserialize<ControlEnvelope>(ControlFrameCodec.SerializerOptions);
            Assert.NotNull(expectedEnvelope);
            AssertEnvelopeEqual(expectedEnvelope, decoded);

            await using var encoded = new MemoryStream();
            await new ControlFrameCodec().WriteAsync(encoded, decoded);
            Assert.Equal(expectedFrame, encoded.ToArray());
        }
    }

    [Fact]
    public async Task SharedArtifactFreezesValidSequencesAndRejectionCoverage()
    {
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(GoldenPath));
        foreach (var sequence in artifact.RootElement.GetProperty("validSequences").EnumerateArray())
        {
            foreach (var vector in sequence.GetProperty("frames").EnumerateArray())
            {
                Assert.Contains(
                    vector.GetProperty("origin").GetString(),
                    new[] { "csharp", "node" });
                var expectedFrame = Convert.FromBase64String(vector.GetProperty("frameBase64").GetString()!);
                var envelope = vector.GetProperty("envelope").Deserialize<ControlEnvelope>(ControlFrameCodec.SerializerOptions);
                Assert.NotNull(envelope);
                AssertEnvelopeEqual(
                    envelope,
                    await new ControlFrameCodec().ReadAsync(new MemoryStream(expectedFrame)));
                await using var encoded = new MemoryStream();
                await new ControlFrameCodec().WriteAsync(encoded, envelope);
                Assert.Equal(expectedFrame, encoded.ToArray());
            }
        }

        var scenarioNames = artifact.RootElement
            .GetProperty("invalidTranscripts")
            .EnumerateArray()
            .Select(vector => vector.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var requiredScenarios = new[]
        {
                "hello-before-challenge",
                "duplicate-challenge",
                "conflicting-hello",
                "wrong-process-id",
                "wrong-creation-time",
                "wrong-challenge-id",
                "cross-generation",
                "challenge-timeout",
                "challenge-cancellation",
                "v1-downgrade",
                "oversize",
                "out-of-order",
        };
        Assert.All(requiredScenarios, name => Assert.Contains(name, scenarioNames));
    }

    [Fact]
    public async Task CSharpRejectsEveryTypedInvalidGoldenEnvelope()
    {
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(GoldenPath));
        foreach (var vector in artifact.RootElement.GetProperty("invalidEnvelopes").EnumerateArray())
        {
            var envelope = vector.GetProperty("envelope");
            var framePayload = JsonSerializer.SerializeToUtf8Bytes(envelope);
            var frame = new byte[sizeof(uint) + framePayload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                frame,
                checked((uint)framePayload.Length));
            framePayload.CopyTo(frame.AsSpan(sizeof(uint)));

            var error = await Assert.ThrowsAsync<ControlProtocolException>(
                () => new ControlFrameCodec().ReadAsync(new MemoryStream(frame)));
            var expected = vector.GetProperty("expectedError").GetString() switch
            {
                "unknown-version" => ControlProtocolErrorCode.UnknownVersion,
                "invalid-envelope" => ControlProtocolErrorCode.InvalidEnvelope,
                "invalid-payload" or "invalid-creation-ticks" => ControlProtocolErrorCode.InvalidPayload,
                var value => throw new InvalidOperationException($"Unknown golden error code: {value}"),
            };
            Assert.Equal(expected, error.Code);
        }
    }

    [Fact]
    public async Task CSharpExecutesEverySharedInvalidTranscriptAgainstProductionProtocolCode()
    {
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(GoldenPath));
        var vectors = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var vector in artifact.RootElement.GetProperty("validFrames").EnumerateArray())
        {
            vectors.Add(vector.GetProperty("name").GetString()!, vector.GetProperty("envelope").Clone());
        }

        foreach (var sequence in artifact.RootElement.GetProperty("validSequences").EnumerateArray())
        {
            foreach (var vector in sequence.GetProperty("frames").EnumerateArray())
            {
                vectors.Add(vector.GetProperty("name").GetString()!, vector.GetProperty("envelope").Clone());
            }
        }

        foreach (var transcript in artifact.RootElement.GetProperty("invalidTranscripts").EnumerateArray())
        {
            var runner = new TranscriptRunner(artifact.RootElement.GetProperty("expectedIdentity"), vectors);
            var actualError = await runner.RunAsync(transcript.GetProperty("events"));
            Assert.Equal(
                transcript.GetProperty("expectedError").GetString(),
                actualError);
        }
    }

    private static string FindGoldenPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory.FullName,
            "desktop",
            "windows",
            "protocol",
            "control-protocol-v2.golden.json");
        Assert.True(File.Exists(path), $"Golden protocol artifact not found: {path}");
        return path;
    }

    private static void AssertEnvelopeEqual(ControlEnvelope expected, ControlEnvelope actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.Type, actual.Type);
        Assert.True(JsonElement.DeepEquals(expected.Payload, actual.Payload));
    }

    private sealed class TranscriptRunner
    {
        private readonly IReadOnlyDictionary<string, JsonElement> _vectors;
        private readonly ControlSessionValidator _validator;
        private bool _challengeSeen;

        public TranscriptRunner(
            JsonElement expectedIdentity,
            IReadOnlyDictionary<string, JsonElement> vectors)
        {
            _vectors = vectors;
            _validator = new ControlSessionValidator(new ExpectedControlIdentity(
                ParseRole(expectedIdentity.GetProperty("role").GetString()!),
                expectedIdentity.GetProperty("generation").GetInt64(),
                expectedIdentity.GetProperty("startupOperationId").GetGuid(),
                expectedIdentity.GetProperty("processId").GetInt32(),
                long.Parse(
                    expectedIdentity.GetProperty("processCreationTimeUtcTicks").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                expectedIdentity.GetProperty("capabilityNonce").GetString()!,
                expectedIdentity.GetProperty("buildIdentity").GetString()!,
                expectedIdentity.GetProperty("challengeId").GetString()!));
        }

        public async Task<string?> RunAsync(JsonElement events)
        {
            foreach (var @event in events.EnumerateArray())
            {
                var kind = @event.GetProperty("kind").GetString();
                if (kind == "timeout")
                {
                    return "timeout";
                }

                if (kind == "cancellation")
                {
                    return "cancelled";
                }

                try
                {
                    if (kind == "rawLength")
                    {
                        var prefix = new byte[sizeof(uint)];
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            prefix,
                            @event.GetProperty("length").GetUInt32());
                        _ = await new ControlFrameCodec().ReadAsync(new MemoryStream(prefix));
                        continue;
                    }

                    Assert.Equal("frame", kind);
                    var envelope = await DecodeEventAsync(@event);
                    var direction = @event.GetProperty("direction").GetString() == "appHostToChild"
                        ? ControlDirection.AppHostToChild
                        : ControlDirection.ChildToAppHost;
                    if (!ControlDirectionPolicy.IsAllowed(envelope.Role, envelope.Type, direction))
                    {
                        return "invalid-direction";
                    }

                    if (!_challengeSeen)
                    {
                        if (envelope.Type != ControlMessageType.IdentityChallenge)
                        {
                            return envelope.Type == ControlMessageType.Hello
                                ? "unexpected-hello"
                                : "unexpected-challenge";
                        }

                        _challengeSeen = true;
                        continue;
                    }

                    if (envelope.Type == ControlMessageType.IdentityChallenge)
                    {
                        return "unexpected-challenge";
                    }

                    var validation = _validator.Validate(envelope);
                    if (validation.Status == ControlValidationStatus.Rejected)
                    {
                        return ToGoldenError(validation.ReasonCode);
                    }
                }
                catch (ControlProtocolException exception)
                {
                    return exception.Code switch
                    {
                        ControlProtocolErrorCode.UnknownVersion => "unknown-version",
                        ControlProtocolErrorCode.FrameTooLarge => "frame-too-large",
                        ControlProtocolErrorCode.InvalidEnvelope => "invalid-envelope",
                        ControlProtocolErrorCode.InvalidPayload => "invalid-payload",
                        _ => exception.Code.ToString(),
                    };
                }
            }

            return null;
        }

        private async Task<ControlEnvelope> DecodeEventAsync(JsonElement @event)
        {
            var name = @event.GetProperty("vector").GetString()!;
            var envelope = JsonNode.Parse(_vectors[name].GetRawText())!;
            if (@event.TryGetProperty("mutations", out var mutations))
            {
                foreach (var mutation in mutations.EnumerateArray())
                {
                    ApplyJsonPointer(
                        envelope,
                        mutation.GetProperty("path").GetString()!,
                        JsonNode.Parse(mutation.GetProperty("value").GetRawText()));
                }
            }

            var payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            var frame = new byte[sizeof(uint) + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
            payload.CopyTo(frame.AsSpan(sizeof(uint)));
            return await new ControlFrameCodec().ReadAsync(new MemoryStream(frame));
        }

        private static void ApplyJsonPointer(JsonNode root, string path, JsonNode? value)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal))
                .ToArray();
            var current = root.AsObject();
            foreach (var segment in segments[..^1])
            {
                current = current[segment]!.AsObject();
            }

            current[segments[^1]] = value;
        }

        private static RuntimeRole ParseRole(string role) => role switch
        {
            "database" => RuntimeRole.Database,
            "server" => RuntimeRole.Server,
            "worker" => RuntimeRole.Worker,
            _ => throw new InvalidOperationException($"Unknown role: {role}"),
        };

        private static string ToGoldenError(ControlValidationReasonCode reason) => reason switch
        {
            ControlValidationReasonCode.UnexpectedHello => "unexpected-hello",
            ControlValidationReasonCode.HelloRequired => "hello-required",
            ControlValidationReasonCode.OperationIdMismatch => "operation-id-mismatch",
            ControlValidationReasonCode.RoleMismatch => "role-mismatch",
            ControlValidationReasonCode.GenerationMismatch => "generation-mismatch",
            ControlValidationReasonCode.ProcessIdMismatch => "process-id-mismatch",
            ControlValidationReasonCode.ProcessCreationTimeMismatch => "process-creation-time-mismatch",
            ControlValidationReasonCode.ChallengeMismatch => "challenge-mismatch",
            ControlValidationReasonCode.CapabilityNonceMismatch => "capability-nonce-mismatch",
            ControlValidationReasonCode.BuildIdentityMismatch => "build-identity-mismatch",
            ControlValidationReasonCode.ConflictingDuplicate => "conflicting-duplicate",
            ControlValidationReasonCode.OutOfOrder => "out-of-order",
            _ => reason.ToString(),
        };
    }
}
