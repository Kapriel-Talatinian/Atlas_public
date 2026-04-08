namespace Atlas.Api.Models;

public enum AtlasRuntimeRole
{
    All,
    Api,
    BotWorker
}

public sealed record AtlasRuntimeContext(
    AtlasRuntimeRole Role,
    string InstanceId,
    string HostName)
{
    public bool CanRunBotLoop => Role is AtlasRuntimeRole.All or AtlasRuntimeRole.BotWorker;

    public static AtlasRuntimeContext FromEnvironment()
    {
        string rawRole = Environment.GetEnvironmentVariable("ATLAS_RUNTIME_ROLE") ?? "all";
        AtlasRuntimeRole role = rawRole.Trim().ToLowerInvariant() switch
        {
            "api" => AtlasRuntimeRole.Api,
            "bot-worker" => AtlasRuntimeRole.BotWorker,
            _ => AtlasRuntimeRole.All
        };

        string instanceId = Environment.GetEnvironmentVariable("ATLAS_INSTANCE_ID")?.Trim() ??
            $"{Environment.MachineName}-{Environment.ProcessId}";

        return new AtlasRuntimeContext(
            Role: role,
            InstanceId: instanceId,
            HostName: Environment.MachineName);
    }
}

public sealed record BotStateRecord(
    string BotKey,
    string StateJson,
    long StateVersion,
    DateTimeOffset LastPersistedAt,
    DateTimeOffset? LastEvaluationAt,
    string LastCycleStatus,
    int LastCycleDurationMs);

public sealed record BotStateSaveRequest(
    string BotKey,
    string StateJson,
    long ExpectedStateVersion,
    DateTimeOffset? LastEvaluationAt,
    string LastCycleStatus,
    int LastCycleDurationMs);

public sealed record BotLeaderLeaseSnapshot(
    string BotKey,
    bool IsLeader,
    string? OwnerInstanceId,
    string? OwnerHostName,
    long FencingToken,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset CheckedAt);

public sealed record BotRuntimeStatusSnapshot(
    string BotKey,
    AtlasRuntimeRole RuntimeRole,
    string InstanceId,
    string HostName,
    string RepositoryBackend,
    string LeaderBackend,
    bool CanRunBotLoop,
    BotLeaderLeaseSnapshot Leader,
    long StateVersion,
    DateTimeOffset? LastPersistedAt,
    DateTimeOffset? LastEvaluationAt,
    string LastCycleStatus,
    int LastCycleDurationMs);

public sealed record BotRuntimeHealthSnapshot(
    string BotKey,
    string Status,
    AtlasRuntimeRole RuntimeRole,
    bool IsLeader,
    string? LeaderInstanceId,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset? LastPersistedAt,
    DateTimeOffset? LastEvaluationAt,
    string LastCycleStatus,
    int LastCycleDurationMs,
    DateTimeOffset Timestamp);
