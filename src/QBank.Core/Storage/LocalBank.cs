using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QBank.Core.Models;
using QBank.Core.Search;

namespace QBank.Core.Storage;

public sealed class BankSearchResult
{
    public Question Question { get; init; } = new();
    public double Score { get; init; }
}

public sealed class BankStats
{
    public int Total { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new();
    public int TotalHits { get; set; }
    public int TodayAdded { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
}

public sealed class ImportResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();

    public string Summary => $"新增 {Added} 条，更新 {Updated} 条，跳过 {Skipped} 条" +
                             (Errors.Count > 0 ? $"，{Errors.Count} 行有问题" : string.Empty);
}

/// <summary>本地题库（SQLite）。题干按归一化后判重，检索用“前缀/片段筛候选 + 相似度精排”。</summary>
public sealed class LocalBank
{
    private const string SelectColumns =
        "id, title, norm_title, norm_prefix, type, options, answer, extra_json, source, tags, created_at, updated_at, hit_count, last_hit_at, fingerprint";

    private readonly string _connectionString;

    public LocalBank(string databasePath)
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
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA synchronous=NORMAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS questions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                title        TEXT NOT NULL,
                norm_title   TEXT NOT NULL,
                norm_prefix  TEXT NOT NULL,
                type         TEXT NOT NULL DEFAULT '',
                options      TEXT NOT NULL DEFAULT '',
                answer       TEXT NOT NULL DEFAULT '',
                extra_json   TEXT,
                source       TEXT,
                tags         TEXT,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                hit_count    INTEGER NOT NULL DEFAULT 0,
                last_hit_at  TEXT,
                fingerprint  TEXT NOT NULL
            );
            """);
        Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS idx_questions_fp ON questions(fingerprint);");
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_questions_prefix ON questions(norm_prefix);");
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_questions_updated ON questions(updated_at DESC);");
    }

    // ---------------- 写入 ----------------

    /// <summary>按指纹判重写入；已存在则更新内容并保留命中次数。</summary>
    public long Upsert(Question question)
    {
        question.Recompute();
        using var c = Open();

        Exec(c, """
            INSERT INTO questions
                (title, norm_title, norm_prefix, type, options, answer, extra_json, source, tags,
                 created_at, updated_at, hit_count, last_hit_at, fingerprint)
            VALUES
                (@title, @norm, @prefix, @type, @options, @answer, @extra, @source, @tags,
                 @created, @updated, @hits, @lastHit, @fp)
            ON CONFLICT(fingerprint) DO UPDATE SET
                title       = excluded.title,
                norm_title  = excluded.norm_title,
                norm_prefix = excluded.norm_prefix,
                type        = excluded.type,
                options     = CASE WHEN excluded.options = '' THEN questions.options ELSE excluded.options END,
                answer      = excluded.answer,
                extra_json  = excluded.extra_json,
                source      = excluded.source,
                tags        = excluded.tags,
                updated_at  = excluded.updated_at;
            """,
            ("@title", question.Title),
            ("@norm", question.NormalizedTitle),
            ("@prefix", question.NormalizedPrefix),
            ("@type", question.Type.ToOcs()),
            ("@options", question.Options ?? string.Empty),
            ("@answer", question.Answer ?? string.Empty),
            ("@extra", (object?)question.ExtraJson),
            ("@source", (object?)question.Source),
            ("@tags", (object?)question.Tags),
            ("@created", Iso(question.CreatedAt)),
            ("@updated", Iso(DateTimeOffset.Now)),
            ("@hits", question.HitCount),
            ("@lastHit", question.LastHitAt is null ? null : Iso(question.LastHitAt.Value)),
            ("@fp", question.Fingerprint));

        return Convert.ToInt64(Scalar(c, "SELECT id FROM questions WHERE fingerprint = @fp", ("@fp", question.Fingerprint)) ?? 0L);
    }

    public int ImportMany(IEnumerable<Question> questions)
    {
        var added = 0;
        foreach (var q in questions)
        {
            if (string.IsNullOrWhiteSpace(q.Title) || string.IsNullOrWhiteSpace(q.Answer)) continue;
            Upsert(q);
            added++;
        }
        return added;
    }

    public void RegisterHit(long id)
    {
        using var c = Open();
        Exec(c, "UPDATE questions SET hit_count = hit_count + 1, last_hit_at = @now WHERE id = @id",
            ("@now", Iso(DateTimeOffset.Now)), ("@id", id));
    }

    public bool Delete(long id)
    {
        using var c = Open();
        return Exec(c, "DELETE FROM questions WHERE id = @id", ("@id", id)) > 0;
    }

    public int DeleteAll()
    {
        using var c = Open();
        return Exec(c, "DELETE FROM questions");
    }

    /// <summary>重新计算所有行的归一化字段与指纹（改了归一化规则后调用）。</summary>
    public int RebuildDerived()
    {
        var all = Query(null, null, 0, int.MaxValue, "id");
        var count = 0;
        foreach (var q in all)
        {
            var normalized = QuestionNormalizer.Normalize(q.Title);
            using var c = Open();
            Exec(c, """
                UPDATE questions SET norm_title = @norm, norm_prefix = @prefix, fingerprint = @fp WHERE id = @id;
                """,
                ("@norm", normalized),
                ("@prefix", QuestionNormalizer.Prefix(normalized)),
                ("@fp", Question.ComputeFingerprint(normalized, q.Type)),
                ("@id", q.Id));
            count++;
        }
        return count;
    }

    // ---------------- 查询 ----------------

    public Question? GetById(long id)
    {
        using var c = Open();
        using var reader = Query(c, $"SELECT {SelectColumns} FROM questions WHERE id = @id", ("@id", id));
        return reader.Read() ? Map(reader.Reader) : null;
    }

    public List<Question> Query(string? keyword, QuestionType? type, int offset, int limit, string order = "updated")
    {
        var sql = new StringBuilder($"SELECT {SelectColumns} FROM questions WHERE 1=1");
        var ps = new List<(string, object?)>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND (title LIKE @kw OR answer LIKE @kw OR IFNULL(tags,'') LIKE @kw)");
            ps.Add(("@kw", "%" + keyword!.Trim() + "%"));
        }
        if (type is not null)
        {
            sql.Append(" AND type = @type");
            ps.Add(("@type", type.Value.ToOcs()));
        }

        sql.Append(order switch
        {
            "hits" => " ORDER BY hit_count DESC, updated_at DESC",
            "created" => " ORDER BY created_at DESC",
            "title" => " ORDER BY title ASC",
            "id" => " ORDER BY id ASC",
            _ => " ORDER BY updated_at DESC",
        });

        if (limit > 0 && limit != int.MaxValue)
        {
            sql.Append(" LIMIT @limit OFFSET @offset");
            ps.Add(("@limit", limit));
            ps.Add(("@offset", offset));
        }

        using var c = Open();
        using var reader = Query(c, sql.ToString(), ps.ToArray());
        var list = new List<Question>();
        while (reader.Read()) list.Add(Map(reader.Reader));
        return list;
    }

    public int Count(string? keyword = null, QuestionType? type = null)
    {
        var sql = new StringBuilder("SELECT COUNT(*) FROM questions WHERE 1=1");
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND (title LIKE @kw OR answer LIKE @kw OR IFNULL(tags,'') LIKE @kw)");
            ps.Add(("@kw", "%" + keyword!.Trim() + "%"));
        }
        if (type is not null)
        {
            sql.Append(" AND type = @type");
            ps.Add(("@type", type.Value.ToOcs()));
        }

        using var c = Open();
        return Convert.ToInt32(Scalar(c, sql.ToString(), ps.ToArray()) ?? 0);
    }

    /// <summary>搜题：先粗筛候选，再按相似度精排。</summary>
    public List<BankSearchResult> Search(string title, QuestionType type, int limit, double minScore, bool registerHit = true)
    {
        var normalized = QuestionNormalizer.Normalize(QuestionNormalizer.StripDecorations(title));
        if (normalized.Length == 0) return new List<BankSearchResult>();

        var candidates = new List<Question>();
        using (var c = Open())
        {
            var prefix = QuestionNormalizer.Prefix(normalized);
            candidates.AddRange(ReadAll(c, $"SELECT {SelectColumns} FROM questions WHERE norm_prefix = @p LIMIT 400", ("@p", prefix)));

            if (candidates.Count < 10)
            {
                var fragment = LongestFingerprintFragment(normalized);
                if (fragment.Length >= 2)
                    candidates.AddRange(ReadAll(c,
                        $"SELECT {SelectColumns} FROM questions WHERE norm_title LIKE @like LIMIT 800",
                        ("@like", "%" + fragment + "%")));
            }

            if (candidates.Count < 10)
                candidates.AddRange(ReadAll(c, $"SELECT {SelectColumns} FROM questions ORDER BY hit_count DESC LIMIT 300"));
        }

        var scored = new List<BankSearchResult>();
        var seen = new HashSet<long>();
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Id)) continue;

            var score = SimilarityScorer.ScoreNormalized(candidate.NormalizedTitle, normalized);
            if (type != QuestionType.Unknown && candidate.Type != QuestionType.Unknown && candidate.Type != type)
                score *= 0.92; // 题型不符略降权，但不直接丢弃

            if (score >= minScore)
                scored.Add(new BankSearchResult { Question = candidate, Score = score });
        }

        var result = scored.OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Question.HitCount)
            .Take(limit)
            .ToList();

        if (registerHit && result.Count > 0)
        {
            using var c = Open();
            foreach (var item in result)
                Exec(c, "UPDATE questions SET hit_count = hit_count + 1, last_hit_at = @now WHERE id = @id",
                    ("@now", Iso(DateTimeOffset.Now)), ("@id", item.Question.Id));
        }

        return result;
    }

    /// <summary>取归一化题干里较有辨识度的一段（跳过开头可能的题号），用于 LIKE 粗筛。</summary>
    private static string LongestFingerprintFragment(string normalized)
    {
        var start = 0;
        while (start < normalized.Length && char.IsDigit(normalized[start])) start++;
        var available = normalized.Length - start;
        if (available <= 0) start = 0;
        var length = Math.Min(available, 8);
        if (length < 2) return normalized.Length >= 2 ? normalized[..2] : string.Empty;
        return normalized.Substring(start, length);
    }

    public BankStats Stats()
    {
        using var c = Open();
        var stats = new BankStats
        {
            Total = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM questions") ?? 0),
            TotalHits = Convert.ToInt32(Scalar(c, "SELECT IFNULL(SUM(hit_count),0) FROM questions") ?? 0),
            TodayAdded = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM questions WHERE created_at >= @from",
                ("@from", Iso(DateTimeOffset.Now.Date))) ?? 0),
        };

        using (var reader = Query(c, "SELECT type, COUNT(*) FROM questions GROUP BY type"))
        {
            while (reader.Read())
            {
                var raw = reader.Reader.IsDBNull(0) ? string.Empty : reader.Reader.GetString(0);
                var name = QuestionTypeExtensions.ParseOcs(raw);
                stats.ByType[name.ToChinese()] = reader.Reader.GetInt32(1);
            }
        }

        var last = Scalar(c, "SELECT MAX(updated_at) FROM questions");
        if (last is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            stats.LastUpdated = dt;

        return stats;
    }

    public List<string> AllTags()
    {
        using var c = Open();
        using var reader = Query(c, "SELECT DISTINCT tags FROM questions WHERE tags IS NOT NULL AND tags <> ''");
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            foreach (var tag in reader.Reader.GetString(0).Split(new[] { ' ', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(tag.Trim());
        }
        return set.ToList();
    }

    // ---------------- 导入导出 ----------------

    public string ExportJson()
    {
        var list = Query(null, null, 0, int.MaxValue, "id").Select(q => new
        {
            title = q.Title,
            type = q.Type.ToOcs(),
            options = q.Options,
            answer = q.Answer,
            source = q.Source,
            tags = q.Tags,
            hitCount = q.HitCount,
            createdAt = Iso(q.CreatedAt),
            updatedAt = Iso(q.UpdatedAt),
        });
        return JsonSerializer.Serialize(new { app = "QBank", version = 1, exportedAt = Iso(DateTimeOffset.Now), questions = list },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
    }

    public string ExportCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("title,type,options,answer,source,tags,hitCount");
        foreach (var q in Query(null, null, 0, int.MaxValue, "id"))
        {
            sb.Append(Csv(q.Title)).Append(',')
              .Append(Csv(q.Type.ToOcs())).Append(',')
              .Append(Csv(q.Options)).Append(',')
              .Append(Csv(q.Answer)).Append(',')
              .Append(Csv(q.Source ?? string.Empty)).Append(',')
              .Append(Csv(q.Tags ?? string.Empty)).Append(',')
              .Append(q.HitCount)
              .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>纯文本格式：每行 <c>题目#答案</c>（也支持 Tab、竖线分隔）。</summary>
    public string ExportTxt()
    {
        var sb = new StringBuilder();
        foreach (var q in Query(null, null, 0, int.MaxValue, "id"))
        {
            if (q.Answer.Contains('\n') || q.Title.Contains('\n')) continue;
            sb.Append(q.Title).Append('#').Append(q.Answer).AppendLine();
        }
        return sb.ToString();
    }

    public ImportResult Import(string content, string format = "auto")
    {
        var result = new ImportResult();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var trimmed = content.TrimStart();
        if (format == "auto")
        {
            if (trimmed.StartsWith('[') || trimmed.StartsWith('{')) format = "json";
            else if (trimmed.Contains('#')) format = "txt";
            else format = "csv";
        }

        switch (format)
        {
            case "json": ImportJson(content, result); break;
            case "csv": ImportCsv(content, result); break;
            default: ImportTxt(content, result); break;
        }
        return result;
    }

    private void ImportJson(string content, ImportResult result)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            result.Errors.Add("JSON 解析失败：" + ex.Message);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var qs))
                root = qs;

            if (root.ValueKind != JsonValueKind.Array)
            {
                result.Errors.Add("JSON 结构应为数组，或带 questions 数组的对象");
                return;
            }

            foreach (var item in root.EnumerateArray())
                ImportOne(item, result);
        }
    }

    private void ImportOne(JsonElement item, ImportResult result)
    {
        string? Get(params string[] names)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in item.EnumerateObject())
                foreach (var n in names)
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString(),
                            JsonValueKind.Null or JsonValueKind.Undefined => null,
                            _ => p.Value.GetRawText(),
                        };
            return null;
        }

        string Text(string? s) => s is null ? string.Empty : s;

        var title = Text(Get("title", "question", "题目", "题干"));
        var answer = Text(Get("answer", "answers", "答案"));
        if (title.Length == 0 || answer.Length == 0)
        {
            result.Skipped++;
            return;
        }

        var typeRaw = Text(Get("type", "题型"));
        var question = new Question
        {
            Title = title.Trim(),
            Answer = answer.Trim(),
            Options = Text(Get("options", "选项")),
            Type = QuestionTypeExtensions.ParseOcs(typeRaw),
            Source = Get("source", "来源") ?? "导入",
            Tags = Get("tags", "标签"),
        };
        if (question.Type == QuestionType.Unknown)
            question.Type = QuestionTypeInference.Infer(question.Title, question.Options);

        Upsert(question);
        result.Added++;
    }

    private void ImportCsv(string content, ImportResult result)
    {
        var rows = ParseCsv(content);
        if (rows.Count == 0) return;

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var hasHeader = header.Contains("title") || header.Contains("题目") || header.Contains("answer") || header.Contains("答案");
        var start = hasHeader ? 1 : 0;

        int Idx(params string[] names)
        {
            if (!hasHeader) return -1;
            for (var i = 0; i < header.Count; i++)
                if (names.Any(n => header[i] == n)) return i;
            return -1;
        }

        var titleIdx = hasHeader ? Math.Max(0, Idx("title", "题目", "题干")) : 0;
        var answerIdx = hasHeader ? Math.Max(1, Idx("answer", "答案")) : 1;
        var optionsIdx = hasHeader ? Idx("options", "选项") : 2;
        var typeIdx = hasHeader ? Idx("type", "题型") : 3;
        var sourceIdx = hasHeader ? Idx("source", "来源") : -1;
        var tagsIdx = hasHeader ? Idx("tags", "标签") : -1;

        for (var r = start; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0) continue;

            string At(int idx) => idx >= 0 && idx < row.Count ? row[idx].Trim() : string.Empty;

            var title = At(titleIdx);
            var answer = At(answerIdx);
            if (title.Length == 0 || answer.Length == 0)
            {
                result.Skipped++;
                continue;
            }

            var question = new Question
            {
                Title = title,
                Answer = answer,
                Options = At(optionsIdx),
                Type = QuestionTypeExtensions.ParseOcs(At(typeIdx)),
                Source = At(sourceIdx).Length > 0 ? At(sourceIdx) : "导入",
                Tags = At(tagsIdx).Length > 0 ? At(tagsIdx) : null,
            };
            if (question.Type == QuestionType.Unknown)
                question.Type = QuestionTypeInference.Infer(question.Title, question.Options);

            Upsert(question);
            result.Added++;
        }
    }

    private void ImportTxt(string content, ImportResult result)
    {
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.Contains('\t') ? '\t' : line.Contains('#') ? '#' : line.Contains('|') ? '|' : '\0';
            if (separator == '\0')
            {
                result.Skipped++;
                continue;
            }

            var parts = line.Split(separator);
            if (parts.Length < 2)
            {
                result.Skipped++;
                continue;
            }

            var title = parts[0].Trim();
            var answer = string.Join(separator == '#' ? "#" : separator.ToString(),
                parts.Skip(1)).Trim(); // 答案本身可能含分隔符（多选答案），全部保留
            if (title.Length == 0 || answer.Length == 0)
            {
                result.Skipped++;
                continue;
            }

            var question = new Question
            {
                Title = title,
                Answer = answer,
                Type = QuestionTypeInference.Infer(title, null),
                Source = "导入",
            };
            Upsert(question);
            result.Added++;
        }
    }

    private static string Csv(string? value)
    {
        var v = value ?? string.Empty;
        return v.Contains('"') || v.Contains(',') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    private static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default: field.Append(ch); break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    // ---------------- 底层助手 ----------------

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTimeOffset.MinValue;

    private static Question Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.IsDBNull(1) ? string.Empty : r.GetString(1),
        NormalizedTitle = r.IsDBNull(2) ? string.Empty : r.GetString(2),
        NormalizedPrefix = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        Type = QuestionTypeExtensions.ParseOcs(r.IsDBNull(4) ? null : r.GetString(4)),
        Options = r.IsDBNull(5) ? string.Empty : r.GetString(5),
        Answer = r.IsDBNull(6) ? string.Empty : r.GetString(6),
        ExtraJson = r.IsDBNull(7) ? null : r.GetString(7),
        Source = r.IsDBNull(8) ? null : r.GetString(8),
        Tags = r.IsDBNull(9) ? null : r.GetString(9),
        CreatedAt = ParseIso(r.IsDBNull(10) ? null : r.GetString(10)),
        UpdatedAt = ParseIso(r.IsDBNull(11) ? null : r.GetString(11)),
        HitCount = r.IsDBNull(12) ? 0 : r.GetInt32(12),
        LastHitAt = r.IsDBNull(13) ? null : ParseIso(r.GetString(13)),
        Fingerprint = r.IsDBNull(14) ? string.Empty : r.GetString(14),
    };

    private static List<Question> ReadAll(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var reader = Query(c, sql, ps);
        var list = new List<Question>();
        while (reader.Read()) list.Add(Map(reader.Reader));
        return list;
    }

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
    /// 执行查询并返回一个「命令 + reader 一起活」的包装。
    /// 必须先执行 ExecuteReader 再释放 SqliteCommand，否则 reader 会被连带关掉。
    /// </summary>
    private static ReaderScope Query(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = BuildCommand(c, sql, ps);
        var reader = cmd.ExecuteReader();
        return new ReaderScope(cmd, reader);
    }

    /// <summary>把 SqliteCommand 与 SqliteDataReader 绑在一起的只读句柄。</summary>
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

        public void Dispose()
        {
            _reader.Dispose();
            _command.Dispose();
        }
    }
}