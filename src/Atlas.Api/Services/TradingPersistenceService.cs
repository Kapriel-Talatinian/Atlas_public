using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.Api.Models;
using Microsoft.Data.Sqlite;

namespace Atlas.Api.Services;

public interface ITradingPersistenceService
{
    void AppendOrderEvent(TradingOrderReport report, string source = "oms");
    void AppendPositionSnapshot(IReadOnlyList<TradingPosition> positions, string source = "positions");
    void AppendRiskEvent(PortfolioRiskSnapshot snapshot, string source = "risk");
    void AppendNotificationEvent(TradingNotification notification);
    void AppendAuditEvent(string category, string message, string payloadJson = "{}");
    IReadOnlyList<PersistedOrderEvent> GetOrderEvents(int limit = 500);
    IReadOnlyList<PersistedPositionEvent> GetPositionEvents(int limit = 500);
    IReadOnlyList<PersistedRiskEvent> GetRiskEvents(int limit = 500);
    IReadOnlyList<PersistedAuditEvent> GetAuditEvents(int limit = 500);
}

public sealed class SqliteTradingPersistenceService : ITradingPersistenceService
{
    private readonly ILogger<SqliteTradingPersistenceService> _logger;
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SqliteTradingPersistenceService(
        IHostEnvironment hostEnvironment,
        ILogger<SqliteTradingPersistenceService> logger)
    {
        _logger = logger;

        string configuredPath = Environment.GetEnvironmentVariable("TRADING_DB_PATH") ?? string.Empty;
        string dbPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(hostEnvironment.ContentRootPath, "data", "trading", "trading.db")
            : configuredPath.Trim();

        if (!Path.IsPathRooted(dbPath))
            dbPath = Path.Combine(hostEnvironment.ContentRootPath, dbPath);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? hostEnvironment.ContentRootPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public void AppendOrderEvent(TradingOrderReport report, string source = "oms")
    {
        try
        {
            string payload = JsonSerializer.Serialize(report, _jsonOptions);
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO order_events(order_id, status, symbol, source, recorded_at, payload_json)
VALUES ($order_id, $status, $symbol, $source, $recorded_at, $payload_json);";
            cmd.Parameters.AddWithValue("$order_id", report.OrderId);
            cmd.Parameters.AddWithValue("$status", report.Status.ToString());
            cmd.Parameters.AddWithValue("$symbol", report.Symbol);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$recorded_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$payload_json", payload);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append order event for {OrderId}", report.OrderId);
        }
    }

    public void AppendRiskEvent(PortfolioRiskSnapshot snapshot, string source = "risk")
    {
        try
        {
            string payload = JsonSerializer.Serialize(snapshot, _jsonOptions);
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO risk_events(source, recorded_at, payload_json)
VALUES ($source, $recorded_at, $payload_json);";
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$recorded_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$payload_json", payload);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append risk event");
        }
    }

    public void AppendPositionSnapshot(IReadOnlyList<TradingPosition> positions, string source = "positions")
    {
        try
        {
            string payload = JsonSerializer.Serialize(positions, _jsonOptions);
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO position_events(source, recorded_at, payload_json)
VALUES ($source, $recorded_at, $payload_json);";
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$recorded_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$payload_json", payload);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append position snapshot");
        }
    }

    public void AppendNotificationEvent(TradingNotification notification)
    {
        try
        {
            string payload = JsonSerializer.Serialize(notification, _jsonOptions);
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO audit_events(category, message, payload_json, recorded_at)
VALUES ($category, $message, $payload_json, $recorded_at);";
            cmd.Parameters.AddWithValue("$category", "notification");
            cmd.Parameters.AddWithValue("$message", $"{notification.Severity}:{notification.Category}:{notification.Message}");
            cmd.Parameters.AddWithValue("$payload_json", payload);
            cmd.Parameters.AddWithValue("$recorded_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append notification event");
        }
    }

    public void AppendAuditEvent(string category, string message, string payloadJson = "{}")
    {
        try
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO audit_events(category, message, payload_json, recorded_at)
VALUES ($category, $message, $payload_json, $recorded_at);";
            cmd.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(category) ? "system" : category.Trim());
            cmd.Parameters.AddWithValue("$message", string.IsNullOrWhiteSpace(message) ? "-" : message.Trim());
            cmd.Parameters.AddWithValue("$payload_json", string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            cmd.Parameters.AddWithValue("$recorded_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append audit event");
        }
    }

    public IReadOnlyList<PersistedOrderEvent> GetOrderEvents(int limit = 500)
    {
        int safeLimit = Math.Clamp(limit, 1, 5000);
        var result = new List<PersistedOrderEvent>(safeLimit);

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT seq, order_id, status, symbol, source, recorded_at, payload_json
FROM order_events
ORDER BY seq DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", safeLimit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long seq = reader.GetInt64(0);
                string orderId = reader.GetString(1);
                string statusRaw = reader.GetString(2);
                string symbol = reader.GetString(3);
                string source = reader.GetString(4);
                string recordedAtRaw = reader.GetString(5);
                string payloadJson = reader.GetString(6);

                TradingOrderReport? report = JsonSerializer.Deserialize<TradingOrderReport>(payloadJson, _jsonOptions);
                if (report is null)
                    continue;

                OrderStatus status = Enum.TryParse<OrderStatus>(statusRaw, true, out var parsed)
                    ? parsed
                    : report.Status;

                DateTimeOffset recordedAt = DateTimeOffset.TryParse(recordedAtRaw, out var parsedDate)
                    ? parsedDate
                    : report.Timestamp;

                result.Add(new PersistedOrderEvent(
                    Sequence: seq,
                    OrderId: orderId,
                    Status: status,
                    Symbol: symbol,
                    Source: source,
                    RecordedAt: recordedAt,
                    Report: report));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch order events");
        }

        return result;
    }

    public IReadOnlyList<PersistedRiskEvent> GetRiskEvents(int limit = 500)
    {
        int safeLimit = Math.Clamp(limit, 1, 5000);
        var result = new List<PersistedRiskEvent>(safeLimit);

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT seq, source, recorded_at, payload_json
FROM risk_events
ORDER BY seq DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", safeLimit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long seq = reader.GetInt64(0);
                string source = reader.GetString(1);
                string recordedAtRaw = reader.GetString(2);
                string payloadJson = reader.GetString(3);

                PortfolioRiskSnapshot? snapshot = JsonSerializer.Deserialize<PortfolioRiskSnapshot>(payloadJson, _jsonOptions);
                if (snapshot is null)
                    continue;

                DateTimeOffset recordedAt = DateTimeOffset.TryParse(recordedAtRaw, out var parsedDate)
                    ? parsedDate
                    : snapshot.Timestamp;

                result.Add(new PersistedRiskEvent(
                    Sequence: seq,
                    Source: source,
                    RecordedAt: recordedAt,
                    Snapshot: snapshot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch risk events");
        }

        return result;
    }

    public IReadOnlyList<PersistedPositionEvent> GetPositionEvents(int limit = 500)
    {
        int safeLimit = Math.Clamp(limit, 1, 5000);
        var result = new List<PersistedPositionEvent>(safeLimit);

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT seq, source, recorded_at, payload_json
FROM position_events
ORDER BY seq DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", safeLimit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long seq = reader.GetInt64(0);
                string source = reader.GetString(1);
                string recordedAtRaw = reader.GetString(2);
                string payloadJson = reader.GetString(3);

                List<TradingPosition>? positions = JsonSerializer.Deserialize<List<TradingPosition>>(payloadJson, _jsonOptions);
                if (positions is null)
                    continue;

                DateTimeOffset recordedAt = DateTimeOffset.TryParse(recordedAtRaw, out var parsedDate)
                    ? parsedDate
                    : DateTimeOffset.UtcNow;

                result.Add(new PersistedPositionEvent(
                    Sequence: seq,
                    Source: source,
                    RecordedAt: recordedAt,
                    Positions: positions));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch position events");
        }

        return result;
    }

    public IReadOnlyList<PersistedAuditEvent> GetAuditEvents(int limit = 500)
    {
        int safeLimit = Math.Clamp(limit, 1, 5000);
        var result = new List<PersistedAuditEvent>(safeLimit);

        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT seq, category, message, payload_json, recorded_at
FROM audit_events
ORDER BY seq DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", safeLimit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long seq = reader.GetInt64(0);
                string category = reader.GetString(1);
                string message = reader.GetString(2);
                string payloadJson = reader.GetString(3);
                string recordedAtRaw = reader.GetString(4);
                DateTimeOffset recordedAt = DateTimeOffset.TryParse(recordedAtRaw, out var parsedDate)
                    ? parsedDate
                    : DateTimeOffset.UtcNow;

                result.Add(new PersistedAuditEvent(
                    Sequence: seq,
                    Category: category,
                    Message: message,
                    PayloadJson: payloadJson,
                    RecordedAt: recordedAt));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch audit events");
        }

        return result;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS order_events(
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id TEXT NOT NULL,
    status TEXT NOT NULL,
    symbol TEXT NOT NULL,
    source TEXT NOT NULL,
    recorded_at TEXT NOT NULL,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS risk_events(
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    recorded_at TEXT NOT NULL,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS position_events(
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    recorded_at TEXT NOT NULL,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_events(
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    category TEXT NOT NULL,
    message TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    recorded_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_order_events_order_id ON order_events(order_id);
CREATE INDEX IF NOT EXISTS idx_order_events_recorded_at ON order_events(recorded_at);
CREATE INDEX IF NOT EXISTS idx_risk_events_recorded_at ON risk_events(recorded_at);
CREATE INDEX IF NOT EXISTS idx_position_events_recorded_at ON position_events(recorded_at);
CREATE INDEX IF NOT EXISTS idx_audit_events_recorded_at ON audit_events(recorded_at);
";
        cmd.ExecuteNonQuery();
    }
}
