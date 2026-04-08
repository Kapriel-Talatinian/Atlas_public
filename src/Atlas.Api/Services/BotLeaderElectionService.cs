using Atlas.Api.Models;
using Npgsql;

namespace Atlas.Api.Services;

public interface IBotLeaderElectionService
{
    string BackendName { get; }
    BotLeaderLeaseSnapshot AcquireOrRenew(string botKey, AtlasRuntimeContext runtime, TimeSpan leaseDuration);
    BotLeaderLeaseSnapshot GetSnapshot(string botKey, AtlasRuntimeContext runtime);
    void Release(string botKey, AtlasRuntimeContext runtime);
}

public sealed class SingleNodeBotLeaderElectionService : IBotLeaderElectionService
{
    public string BackendName => "single-node";

    public BotLeaderLeaseSnapshot AcquireOrRenew(string botKey, AtlasRuntimeContext runtime, TimeSpan leaseDuration)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new BotLeaderLeaseSnapshot(
            BotKey: botKey,
            IsLeader: runtime.CanRunBotLoop,
            OwnerInstanceId: runtime.CanRunBotLoop ? runtime.InstanceId : null,
            OwnerHostName: runtime.CanRunBotLoop ? runtime.HostName : null,
            FencingToken: runtime.CanRunBotLoop ? 1 : 0,
            LeaseUntil: runtime.CanRunBotLoop ? now.Add(leaseDuration) : null,
            CheckedAt: now);
    }

    public BotLeaderLeaseSnapshot GetSnapshot(string botKey, AtlasRuntimeContext runtime) =>
        AcquireOrRenew(botKey, runtime, TimeSpan.FromSeconds(15));

    public void Release(string botKey, AtlasRuntimeContext runtime)
    {
    }
}

public sealed class PostgresBotLeaderElectionService : IBotLeaderElectionService
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresBotLeaderElectionService> _logger;

    public PostgresBotLeaderElectionService(ILogger<PostgresBotLeaderElectionService> logger)
    {
        _logger = logger;
        _connectionString = Environment.GetEnvironmentVariable("BOT_RUNTIME_DB_CONNECTION_STRING")?.Trim() ??
            throw new InvalidOperationException("BOT_RUNTIME_DB_CONNECTION_STRING is required for Postgres leader election.");
        Initialize();
    }

    public string BackendName => "postgres";

    public BotLeaderLeaseSnapshot AcquireOrRenew(string botKey, AtlasRuntimeContext runtime, TimeSpan leaseDuration)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset leaseUntil = now.Add(leaseDuration);

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var seed = connection.CreateCommand())
            {
                seed.Transaction = transaction;
                seed.CommandText = @"
INSERT INTO atlas_bot_leader_lock(bot_key, fencing_token, updated_at)
VALUES ($bot_key, 0, now())
ON CONFLICT (bot_key) DO NOTHING;";
                seed.Parameters.AddWithValue("$bot_key", botKey);
                seed.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
WITH current_lock AS (
    SELECT bot_key, owner_instance_id, owner_hostname, fencing_token, lease_until
    FROM atlas_bot_leader_lock
    WHERE bot_key = $bot_key
    FOR UPDATE
)
UPDATE atlas_bot_leader_lock AS l
SET owner_instance_id = $instance_id,
    owner_hostname = $host_name,
    fencing_token = CASE
        WHEN current_lock.owner_instance_id = $instance_id THEN current_lock.fencing_token
        ELSE current_lock.fencing_token + 1
    END,
    lease_until = $lease_until,
    last_heartbeat_at = $now,
    updated_at = $now
FROM current_lock
WHERE l.bot_key = current_lock.bot_key
  AND (
      current_lock.owner_instance_id = $instance_id
      OR current_lock.lease_until IS NULL
      OR current_lock.lease_until <= $now)
RETURNING l.owner_instance_id, l.owner_hostname, l.fencing_token, l.lease_until;";
                cmd.Parameters.AddWithValue("$bot_key", botKey);
                cmd.Parameters.AddWithValue("$instance_id", runtime.InstanceId);
                cmd.Parameters.AddWithValue("$host_name", runtime.HostName);
                cmd.Parameters.AddWithValue("$lease_until", leaseUntil);
                cmd.Parameters.AddWithValue("$now", now);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var snapshot = new BotLeaderLeaseSnapshot(
                        BotKey: botKey,
                        IsLeader: true,
                        OwnerInstanceId: reader.IsDBNull(0) ? null : reader.GetString(0),
                        OwnerHostName: reader.IsDBNull(1) ? null : reader.GetString(1),
                        FencingToken: reader.GetInt64(2),
                        LeaseUntil: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                        CheckedAt: now);
                    transaction.Commit();
                    return snapshot;
                }
            }

            using (var fallback = connection.CreateCommand())
            {
                fallback.Transaction = transaction;
                fallback.CommandText = @"
SELECT owner_instance_id, owner_hostname, fencing_token, lease_until
FROM atlas_bot_leader_lock
WHERE bot_key = $bot_key;";
                fallback.Parameters.AddWithValue("$bot_key", botKey);
                using var reader = fallback.ExecuteReader();
                if (reader.Read())
                {
                    var snapshot = new BotLeaderLeaseSnapshot(
                        BotKey: botKey,
                        IsLeader: false,
                        OwnerInstanceId: reader.IsDBNull(0) ? null : reader.GetString(0),
                        OwnerHostName: reader.IsDBNull(1) ? null : reader.GetString(1),
                        FencingToken: reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        LeaseUntil: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                        CheckedAt: now);
                    transaction.Commit();
                    return snapshot;
                }
            }

            transaction.Commit();
            return new BotLeaderLeaseSnapshot(botKey, false, null, null, 0, null, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire or renew leader lease for {BotKey}", botKey);
            return new BotLeaderLeaseSnapshot(botKey, false, null, null, 0, null, now);
        }
    }

    public BotLeaderLeaseSnapshot GetSnapshot(string botKey, AtlasRuntimeContext runtime)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT owner_instance_id, owner_hostname, fencing_token, lease_until
FROM atlas_bot_leader_lock
WHERE bot_key = $bot_key;";
            cmd.Parameters.AddWithValue("$bot_key", botKey);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return new BotLeaderLeaseSnapshot(botKey, false, null, null, 0, null, now);

            string? ownerInstanceId = reader.IsDBNull(0) ? null : reader.GetString(0);
            DateTimeOffset? leaseUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
            bool isLeader = ownerInstanceId == runtime.InstanceId && leaseUntil > now;

            return new BotLeaderLeaseSnapshot(
                BotKey: botKey,
                IsLeader: isLeader,
                OwnerInstanceId: ownerInstanceId,
                OwnerHostName: reader.IsDBNull(1) ? null : reader.GetString(1),
                FencingToken: reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                LeaseUntil: leaseUntil,
                CheckedAt: now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get leader snapshot for {BotKey}", botKey);
            return new BotLeaderLeaseSnapshot(botKey, false, null, null, 0, null, now);
        }
    }

    public void Release(string botKey, AtlasRuntimeContext runtime)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
UPDATE atlas_bot_leader_lock
SET lease_until = $now,
    updated_at = $now,
    last_heartbeat_at = $now
WHERE bot_key = $bot_key
  AND owner_instance_id = $instance_id;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow);
            cmd.Parameters.AddWithValue("$bot_key", botKey);
            cmd.Parameters.AddWithValue("$instance_id", runtime.InstanceId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release leader lease for {BotKey}", botKey);
        }
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS atlas_bot_leader_lock (
    bot_key           text PRIMARY KEY,
    owner_instance_id text,
    owner_hostname    text,
    fencing_token     bigint NOT NULL DEFAULT 0,
    lease_until       timestamptz,
    last_heartbeat_at timestamptz,
    updated_at        timestamptz NOT NULL DEFAULT now()
);";
        cmd.ExecuteNonQuery();
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
