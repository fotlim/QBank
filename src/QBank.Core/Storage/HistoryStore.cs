using System.Globalization;
using Microsoft.Data.Sqlite;
using QBank.Core.Models;

namespace QBank.Core.Storage;

public sealed class HistoryRecord
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Options { get; set; } = string.Empty;
    public QuestionType Type { get; set; }
    public string? Answer { get; set; }
    public string? SourceName { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public double Score { get; set; }
    public long ElapsedMs { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool SavedToBank { get; set; }

    public string KindText => SourceKind switch
    {
        "cache" => "本地缓存",
        "local" => "本地题库",
        "lan" => "局域网",
        "ocs" => "OCS题库",
        "ai" => "AI模型",
        _ => SourceKind,
    };

    public string TimeText => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
}

public sealed class SourceStat
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Hits { get; set; }
    public double AvgScore { get; set; }
    public double AvgElapsedMs { get; set; }

    public double HitRate => Count == 0 ? 0 : (double)Hits / Count;
    public string HitRateText => HitRate.ToString("P0");
}

public sealed class HistoryStats
{
    public int Total { get; set; }
    public int Today { get; set; }
    public int Week { get; set; }
    public int SuccessCount { get; set; }
    public double SuccessRate => Total == 0 ? 0 : (double)SuccessCount / Total;
    public double AvgElapsedMs { get; set; }
    public List<SourceStat> BySource { get; set; } = new();

    public string SuccessRateText => SuccessRate.ToString("P0");
    public string AvgElapsedText => AvgElapsedMs <= 0 ? "-" : $"{AvgElapsedMs:F0} ms";
}

/// <summary>搜题记录与统计。</summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) AppPaths.EnsureDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        EnsureSchema();
    }

    public string DatabasePath { get; }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS search_history (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                title       TEXT NOT NULL,
                options     TEXT NOT NULL DEFAULT '',
                type        TEXT NOT NULL DEFAULT '',
                answer      TEXT,
                source_name TEXT,
                source_kind TEXT NOT NULL DEFAULT '',
                score       REAL NOT NULL DEFAULT 0,
                elapsed_ms  INTEGER NOT NULL DEFAULT 0,
                success     INTEGER NOT NULL DEFAULT 0,
                error       TEXT,
                created_at  TEXT NOT NULL,
                saved_to_bank INTEGER NOT NULL DEFAULT 0
            );
            """);
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_history_created ON search_history(created_at DESC);");
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_history_kind ON search_history(source_kind);");
    }

    public long Add(HistoryRecord record)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO search_history
                (title, options, type, answer, source_name, source_kind, score, elapsed_ms, success, error, created_at, saved_to_bank)
            VALUES
                (@title, @options, @type, @answer, @source, @kind, @score, @elapsed, @success, @error, @created, @saved);
            """,
            ("@title", record.Title),
            ("@options", record.Options ?? string.Empty),
            ("@type", record.Type.ToOcs()),
            ("@answer", (object?)record.Answer),
            ("@source", (object?)record.SourceName),
            ("@kind", record.SourceKind),
            ("@score", record.Score),
            ("@elapsed", record.ElapsedMs),
            ("@success", record.Success ? 1 : 0),
            ("@error", (object?)record.Error),
            ("@created", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("@saved", record.SavedToBank ? 1 : 0));

        return Convert.ToInt64(Scalar(c, "SELECT last_insert_rowid()") ?? 0L);
    }

    public void MarkSaved(long id, bool saved = true)
    {
        using var c = Open();
        Exec(c, "UPDATE search_history SET saved_to_bank = @s WHERE id = @id", ("@s", saved ? 1 : 0), ("@id", id));
    }

    public List<HistoryRecord> Query(
        string? keyword = null,
        string? sourceKind = null,
        bool onlySuccess = false,
        DateTimeOffset? from = null,
        int offset = 0,
        int limit = 200)
    {
        var sql = new System.Text.StringBuilder("SELECT id, title, options, type, answer, source_name, source_kind, score, elapsed_ms, success, error, created_at, saved_to_bank FROM search_history WHERE 1=1");
        var ps = new List<(string, object?)>();
        AppendFilters(sql, ps, keyword, sourceKind, onlySuccess, from);

        sql.Append(" ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset");
        ps.Add(("@limit", limit));
        ps.Add(("@offset", offset));

        using var c = Open();
        using var reader = Query(c, sql.ToString(), ps.ToArray());
        var list = new List<HistoryRecord>();
        while (reader.Read())
        {
            list.Add(new HistoryRecord
            {
                Id = reader.GetInt64(0),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Options = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Type = QuestionTypeExtensions.ParseOcs(reader.IsDBNull(3) ? null : reader.GetString(3)),
                Answer = reader.IsDBNull(4) ? null : reader.GetString(4),
                SourceName = reader.IsDBNull(5) ? null : reader.GetString(5),
                SourceKind = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Score = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                ElapsedMs = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                Success = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                Error = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = reader.IsDBNull(11) ? DateTimeOffset.MinValue : ParseIso(reader.GetString(11)),
                SavedToBank = !reader.IsDBNull(12) && reader.GetInt32(12) == 1,
            });
        }
        return list;
    }

    /// <summary>和 Query 用同一套筛选条件，只算总数（分页用）。</summary>
    public int Count(
        string? keyword = null,
        string? sourceKind = null,
        bool onlySuccess = false,
        DateTimeOffset? from = null)
    {
        var sql = new System.Text.StringBuilder("SELECT COUNT(*) FROM search_history WHERE 1=1");
        var ps = new List<(string, object?)>();
        AppendFilters(sql, ps, keyword, sourceKind, onlySuccess, from);

        using var c = Open();
        return Convert.ToInt32(Scalar(c, sql.ToString(), ps.ToArray()) ?? 0);
    }

    private static void AppendFilters(
        System.Text.StringBuilder sql,
        List<(string, object?)> ps,
        string? keyword,
        string? sourceKind,
        bool onlySuccess,
        DateTimeOffset? from)
    {
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND (title LIKE @kw OR IFNULL(answer,'') LIKE @kw)");
            ps.Add(("@kw", "%" + keyword!.Trim() + "%"));
        }
        if (!string.IsNullOrWhiteSpace(sourceKind))
        {
            sql.Append(" AND source_kind = @kind");
            ps.Add(("@kind", sourceKind!));
        }
        if (onlySuccess)
            sql.Append(" AND success = 1");
        if (from is not null)
        {
            sql.Append(" AND created_at >= @from");
            ps.Add(("@from", from.Value.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public HistoryStats Stats()
    {
        using var c = Open();
        var now = DateTimeOffset.Now;
        var startOfToday = new DateTimeOffset(now.Date, now.Offset);
        var startOfWeek = startOfToday.AddDays(-6);

        var stats = new HistoryStats
        {
            Total = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM search_history") ?? 0),
            Today = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM search_history WHERE created_at >= @from",
                ("@from", startOfToday.ToString("O", CultureInfo.InvariantCulture))) ?? 0),
            Week = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM search_history WHERE created_at >= @from",
                ("@from", startOfWeek.ToString("O", CultureInfo.InvariantCulture))) ?? 0),
            SuccessCount = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM search_history WHERE success = 1") ?? 0),
            AvgElapsedMs = Convert.ToDouble(Scalar(c, "SELECT IFNULL(AVG(elapsed_ms),0) FROM search_history") ?? 0d),
        };

        using (var reader = Query(c, """
            SELECT IFNULL(source_name,'未知'), source_kind, COUNT(*) AS c,
                   IFNULL(SUM(success),0) AS hits,
                   IFNULL(AVG(score),0) AS avg_score,
                   IFNULL(AVG(elapsed_ms),0) AS avg_ms
            FROM search_history
            GROUP BY source_name, source_kind
            ORDER BY c DESC
            LIMIT 30;
            """))
        {
            while (reader.Read())
            {
                stats.BySource.Add(new SourceStat
                {
                    Name = reader.IsDBNull(0) ? "未知" : reader.GetString(0),
                    Kind = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Count = reader.GetInt32(2),
                    Hits = reader.GetInt32(3),
                    AvgScore = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    AvgElapsedMs = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                });
            }
        }

        return stats;
    }

    /// <summary>清理记录：before 为空则清空全部；返回删除条数。</summary>
    public int Clear(DateTimeOffset? before = null)
    {
        using var c = Open();
        return before is null
            ? Exec(c, "DELETE FROM search_history")
            : Exec(c, "DELETE FROM search_history WHERE created_at < @before",
                ("@before", before.Value.ToString("O", CultureInfo.InvariantCulture)));
    }

    /// <summary>只保留最近 N 天的记录。</summary>
    public int Prune(int keepDays)
    {
        if (keepDays <= 0) return 0;
        return Clear(DateTimeOffset.Now.AddDays(-keepDays));
    }

    private static DateTimeOffset ParseIso(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTimeOffset.MinValue;

    private static SqliteCommand BuildCommand(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = BuildCommand(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = BuildCommand(c, sql, ps);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// 执行查询并返回「命令 + reader 一起活」的包装。
    /// 必须先 ExecuteReader 再释放 SqliteCommand，否则 reader 会被连带关掉（Invalid attempt to call Read when reader is closed）。
    /// </summary>
    private static ReaderScope Query(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = BuildCommand(c, sql, ps);
        var reader = cmd.ExecuteReader();
        return new ReaderScope(cmd, reader);
    }

    /// <summary>转发到 SqliteDataReader 的只读句柄，读完随 using 一起释放。</summary>
    private sealed class ReaderScope : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteDataReader _reader;

        public ReaderScope(SqliteCommand command, SqliteDataReader reader)
        {
            _command = command;
            _reader = reader;
        }

        public SqliteDataReader Reader => _reader;

        public bool Read() => _reader.Read();

        public bool IsDBNull(int i) => _reader.IsDBNull(i);

        public string GetString(int i) => _reader.GetString(i);

        public long GetInt64(int i) => _reader.GetInt64(i);

        public int GetInt32(int i) => _reader.GetInt32(i);

        public double GetDouble(int i) => _reader.GetDouble(i);

        public void Dispose()
        {
            _reader.Dispose();
            _command.Dispose();
        }
    }
}