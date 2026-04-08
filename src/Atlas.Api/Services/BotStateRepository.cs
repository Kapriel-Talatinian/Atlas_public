using System.Text.Json;
using Atlas.Api.Models;
using Npgsql;

namespace Atlas.Api.Services;

public interface IBotStateRepository
{
    string BackendName { get; }
    BotStateRecord? Load(string botKey);
    BotStateRecord Save(BotStateSaveRequest request);
}

public sealed class BotStateConflictException : InvalidOperationException
{
    public BotStateConflictException(string botKey)
        : base($"Bot runtime state conflict for {botKey}.")
    {
    }
}

public sealed class FileBotStateRepository : IBotStateRepository
{
    private sealed class FileEnvelope
    {
        public string BotKey { get; set; } = "MULTI";
        public long StateVersion { get; set; }
        public DateTimeOffset LastPersistedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastEvaluationAt { get; set; }
        public string LastCycleStatus { get; set; } = "cold";
        public int LastCycleDurationMs { get; set; }
        public string StateJson { get; set; } = "{}";
    }

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<FileBotStateRepository> _logger;
    private readonly string _directory;

    public FileBotStateRepository(IHostEnvironment hostEnvironment, ILogger<FileBotStateRepository> logger)
    {
        _logger = logger;
        string configured = Environment.GetEnvironmentVariable("EXPERIMENTAL_BOT_STATE_DIR") ?? string.Empty;
        _directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(hostEnvironment.ContentRootPath, "data", "experimental-bot")
            : configured.Trim();
        Directory.CreateDirectory(_directory);
    }

    public string BackendName => "file";

    public BotStateRecord? Load(string botKey)
    {
        string path = ResolvePath(botKey);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            FileEnvelope? envelope = JsonSerializer.Deserialize<FileEnvelope>(json, _jsonOptions);
            if (envelope is null)
                return null;

            return ToRecord(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load bot runtime state from {Path}", path);
            return null;
        }
    }

    public BotStateRecord Save(BotStateSaveRequest request)
    {
        string path = ResolvePath(request.BotKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _directory);

        FileEnvelope envelope;
        if (File.Exists(path))
        {
            string currentJson = File.ReadAllText(path);
            FileEnvelope? current = string.IsNullOrWhiteSpace(currentJson)
                ? null
                : JsonSerializer.Deserialize<FileEnvelope>(currentJson, _jsonOptions);

            long currentVersion = current?.StateVersion ?? 0;
            if (currentVersion != request.ExpectedStateVersion)
                throw new BotStateConflictException(request.BotKey);

            envelope = new FileEnvelope
            {
                BotKey = request.BotKey,
                StateVersion = currentVersion + 1,
                LastPersistedAt = DateTimeOffset.UtcNow,
                LastEvaluationAt = request.LastEvaluationAt,
                LastCycleStatus = string.IsNullOrWhiteSpace(request.LastCycleStatus) ? "cold" : request.LastCycleStatus.Trim(),
                LastCycleDurationMs = Math.Max(0, request.LastCycleDurationMs),
                StateJson = request.StateJson
            };
        }
        else
        {
            if (request.ExpectedStateVersion != 0)
                throw new BotStateConflictException(request.BotKey);

            envelope = new FileEnvelope
            {
                BotKey = request.BotKey,
                StateVersion = 1,
                LastPersistedAt = DateTimeOffset.UtcNow,
                LastEvaluationAt = request.LastEvaluationAt,
                LastCycleStatus = string.IsNullOrWhiteSpace(request.LastCycleStatus) ? "cold" : request.LastCycleStatus.Trim(),
                LastCycleDurationMs = Math.Max(0, request.LastCycleDurationMs),
                StateJson = request.StateJson
            };
        }

        File.WriteAllText(path, JsonSerializer.Serialize(envelope, _jsonOptions));
        return ToRecord(envelope);
    }

    private string ResolvePath(string botKey) =>
        Path.Combine(_directory, $"{botKey.Trim().ToLowerInvariant()}-runtime.json");

    private static BotStateRecord ToRecord(FileEnvelope envelope) => new(
        BotKey: envelope.BotKey,
        StateJson: envelope.StateJson,
        StateVersion: envelope.StateVersion,
        LastPersistedAt: envelope.LastPersistedAt,
        LastEvaluationAt: envelope.LastEvaluationAt,
        LastCycleStatus: envelope.LastCycleStatus,
        LastCycleDurationMs: envelope.LastCycleDurationMs);
}

public sealed class PostgresBotStateRepository : IBotStateRepository
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresBotStateRepository> _logger;

    public PostgresBotStateRepository(ILogger<PostgresBotStateRepository> logger)
    {
        _logger = logger;
        _connectionString = Environment.GetEnvironmentVariable("BOT_RUNTIME_DB_CONNECTION_STRING")?.Trim() ??
            throw new InvalidOperationException("BOT_RUNTIME_DB_CONNECTION_STRING is required for Postgres bot state repository.");
        Initialize();
    }

    public string BackendName => "postgres";

    public BotStateRecord? Load(string botKey)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT bot_key,
       state_json::text,
       state_version,
       last_persisted_at,
       last_evaluation_at,
       last_cycle_status,
       last_cycle_duration_ms
FROM atlas_bot_runtime_state
WHERE bot_key = $bot_key;";
            cmd.Parameters.AddWithValue("$bot_key", botKey);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new BotStateRecord(
                BotKey: reader.GetString(0),
                StateJson: reader.GetString(1),
                StateVersion: reader.GetInt64(2),
                LastPersistedAt: reader.GetFieldValue<DateTimeOffset>(3),
                LastEvaluationAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                LastCycleStatus: reader.IsDBNull(5) ? "cold" : reader.GetString(5),
                LastCycleDurationMs: reader.IsDBNull(6) ? 0 : reader.GetInt32(6));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load bot runtime state for {BotKey}", botKey);
            return null;
        }
    }

    public BotStateRecord Save(BotStateSaveRequest request)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO atlas_bot_runtime_state(
    bot_key,
    state_json,
    state_version,
    last_persisted_at,
    last_evaluation_at,
    last_cycle_status,
    last_cycle_duration_ms,
    updated_at)
VALUES (
    $bot_key,
    CAST($state_json AS jsonb),
    $insert_version,
    $last_persisted_at,
    $last_evaluation_at,
    $last_cycle_status,
    $last_cycle_duration_ms,
    $last_persisted_at)
ON CONFLICT (bot_key) DO UPDATE
SET state_json = EXCLUDED.state_json,
    state_version = atlas_bot_runtime_state.state_version + 1,
    last_persisted_at = EXCLUDED.last_persisted_at,
    last_evaluation_at = EXCLUDED.last_evaluation_at,
    last_cycle_status = EXCLUDED.last_cycle_status,
    last_cycle_duration_ms = EXCLUDED.last_cycle_duration_ms,
    updated_at = EXCLUDED.updated_at
WHERE atlas_bot_runtime_state.state_version = $expected_version
RETURNING bot_key,
          state_json::text,
          state_version,
          last_persisted_at,
          last_evaluation_at,
          last_cycle_status,
          last_cycle_duration_ms;";
            cmd.Parameters.AddWithValue("$bot_key", request.BotKey);
            cmd.Parameters.AddWithValue("$state_json", request.StateJson);
            cmd.Parameters.AddWithValue("$insert_version", request.ExpectedStateVersion + 1);
            cmd.Parameters.AddWithValue("$expected_version", request.ExpectedStateVersion);
            cmd.Parameters.AddWithValue("$last_persisted_at", DateTimeOffset.UtcNow);
            cmd.Parameters.AddWithValue("$last_evaluation_at", request.LastEvaluationAt ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$last_cycle_status", string.IsNullOrWhiteSpace(request.LastCycleStatus) ? "cold" : request.LastCycleStatus.Trim());
            cmd.Parameters.AddWithValue("$last_cycle_duration_ms", Math.Max(0, request.LastCycleDurationMs));

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new BotStateConflictException(request.BotKey);

            return new BotStateRecord(
                BotKey: reader.GetString(0),
                StateJson: reader.GetString(1),
                StateVersion: reader.GetInt64(2),
                LastPersistedAt: reader.GetFieldValue<DateTimeOffset>(3),
                LastEvaluationAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                LastCycleStatus: reader.IsDBNull(5) ? "cold" : reader.GetString(5),
                LastCycleDurationMs: reader.IsDBNull(6) ? 0 : reader.GetInt32(6));
        }
        catch (BotStateConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save bot runtime state for {BotKey}", request.BotKey);
            throw;
        }
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS atlas_bot_runtime_state (
    bot_key                text PRIMARY KEY,
    state_json             jsonb NOT NULL DEFAULT '{}'::jsonb,
    state_version          bigint NOT NULL DEFAULT 0,
    last_persisted_at      timestamptz NOT NULL DEFAULT now(),
    last_evaluation_at     timestamptz,
    last_cycle_status      text NOT NULL DEFAULT 'cold',
    last_cycle_duration_ms integer NOT NULL DEFAULT 0,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now()
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
