using System.Globalization;
using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClaudeUsageMonitor.Core.Logs;

public sealed record FileProgress(long Offset, DateTime LastModifiedUtc);

public sealed record TimelinePoint(DateTimeOffset Timestamp, long Tokens);

/// <summary>集計 DB（%LOCALAPPDATA%\ClaudeUsageMonitor\usage.db）。</summary>
public sealed class UsageRepository : IDisposable
{
    private const int SchemaVersion = 1;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;

    public UsageRepository(string databasePath)
    {
        try
        {
            _connection = Open(databasePath);
        }
        catch (SqliteException ex)
        {
            // DB が壊れている場合は作り直す（ログから再集計できるため）
            AppLog.Error("集計 DB を開けなかったため再作成します", ex);
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(databasePath + suffix);
            _connection = Open(databasePath);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        try
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
            var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version != SchemaVersion)
            {
                Execute(connection, """
                    DROP TABLE IF EXISTS source_files;
                    DROP TABLE IF EXISTS usage_records;
                    CREATE TABLE source_files (
                      path          TEXT PRIMARY KEY,
                      offset        INTEGER NOT NULL,
                      last_modified TEXT NOT NULL
                    );
                    CREATE TABLE usage_records (
                      message_id            TEXT NOT NULL,
                      request_id            TEXT NOT NULL,
                      timestamp_utc         TEXT NOT NULL,
                      session_id            TEXT,
                      project_path          TEXT,
                      model                 TEXT,
                      is_sidechain          INTEGER,
                      input_tokens          INTEGER,
                      output_tokens         INTEGER,
                      cache_write_5m_tokens INTEGER,
                      cache_write_1h_tokens INTEGER,
                      cache_read_tokens     INTEGER,
                      web_search_requests   INTEGER,
                      PRIMARY KEY (message_id, request_id)
                    );
                    CREATE INDEX idx_usage_ts ON usage_records(timestamp_utc);
                    """);
                Execute(connection, $"PRAGMA user_version = {SchemaVersion};");
            }
            // 壊れていれば例外になる
            Scalar(connection, "SELECT COUNT(*) FROM source_files;");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return connection;
    }

    public Dictionary<string, FileProgress> LoadFileProgress()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, FileProgress>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, offset, last_modified FROM source_files;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var modified = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                result[reader.GetString(0)] = new FileProgress(reader.GetInt64(1), modified);
            }
            return result;
        }
    }

    /// <summary>レコードと読み込み位置を1トランザクションで保存する。</summary>
    public void SaveChunk(string path, FileProgress progress, IReadOnlyCollection<UsageRecord> records)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();

            if (records.Count > 0)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = tx;
                // ストリーミング中は同じ応答が複数行に記録されるため、後の行（最終値）で置き換える
                insert.CommandText = """
                    INSERT OR REPLACE INTO usage_records VALUES
                    ($mid, $rid, $ts, $sid, $proj, $model, $side, $in, $out, $cw5, $cw1, $cr, $ws);
                    """;
                var p = new[] { "$mid", "$rid", "$ts", "$sid", "$proj", "$model", "$side", "$in", "$out", "$cw5", "$cw1", "$cr", "$ws" }
                    .Select(name =>
                    {
                        // 型は値から推論させる
                        var parameter = insert.CreateParameter();
                        parameter.ParameterName = name;
                        insert.Parameters.Add(parameter);
                        return parameter;
                    }).ToArray();
                foreach (var r in records)
                {
                    p[0].Value = r.MessageId;
                    p[1].Value = r.RequestId;
                    p[2].Value = FormatTimestamp(r.Timestamp);
                    p[3].Value = (object?)r.SessionId ?? DBNull.Value;
                    p[4].Value = (object?)r.ProjectPath ?? DBNull.Value;
                    p[5].Value = (object?)r.Model ?? DBNull.Value;
                    p[6].Value = r.IsSidechain ? 1 : 0;
                    p[7].Value = r.InputTokens;
                    p[8].Value = r.OutputTokens;
                    p[9].Value = r.CacheWrite5mTokens;
                    p[10].Value = r.CacheWrite1hTokens;
                    p[11].Value = r.CacheReadTokens;
                    p[12].Value = r.WebSearchRequests;
                    insert.ExecuteNonQuery();
                }
            }

            using var upsert = _connection.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = "INSERT OR REPLACE INTO source_files VALUES ($path, $offset, $mod);";
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$offset", progress.Offset);
            upsert.Parameters.AddWithValue("$mod", progress.LastModifiedUtc.ToString("O", CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();

            tx.Commit();
        }
    }

    public TokenTotals SumTokens(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using var cmd = RangeCommand("""
                SELECT IFNULL(SUM(input_tokens),0), IFNULL(SUM(output_tokens),0),
                       IFNULL(SUM(cache_write_5m_tokens + cache_write_1h_tokens),0), IFNULL(SUM(cache_read_tokens),0)
                FROM usage_records WHERE timestamp_utc >= $from AND timestamp_utc < $to;
                """, from, to);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new TokenTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }
    }

    public IReadOnlyList<NamedTotal> TotalsBy(string column, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        if (column is not ("model" or "project_path"))
            throw new ArgumentOutOfRangeException(nameof(column));

        lock (_gate)
        {
            using var cmd = RangeCommand($"""
                SELECT IFNULL({column}, '(不明)') AS name,
                       SUM(input_tokens + output_tokens + cache_write_5m_tokens + cache_write_1h_tokens + cache_read_tokens) AS total
                FROM usage_records WHERE timestamp_utc >= $from AND timestamp_utc < $to
                GROUP BY name ORDER BY total DESC LIMIT {limit};
                """, from, to);
            using var reader = cmd.ExecuteReader();
            var result = new List<NamedTotal>();
            while (reader.Read())
                result.Add(new NamedTotal(reader.GetString(0), reader.GetInt64(1)));
            return result;
        }
    }

    public IReadOnlyList<TimelinePoint> Timeline(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using var cmd = RangeCommand("""
                SELECT timestamp_utc,
                       input_tokens + output_tokens + cache_write_5m_tokens + cache_write_1h_tokens + cache_read_tokens
                FROM usage_records WHERE timestamp_utc >= $from AND timestamp_utc < $to
                ORDER BY timestamp_utc;
                """, from, to);
            using var reader = cmd.ExecuteReader();
            var result = new List<TimelinePoint>();
            while (reader.Read())
            {
                var ts = DateTimeOffset.ParseExact(reader.GetString(0), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                result.Add(new TimelinePoint(ts, reader.GetInt64(1)));
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _connection.Dispose();
    }

    private SqliteCommand RangeCommand(string sql, DateTimeOffset from, DateTimeOffset to)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", FormatTimestamp(from));
        cmd.Parameters.AddWithValue("$to", FormatTimestamp(to));
        return cmd;
    }

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
