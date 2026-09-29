using ClinicalAbstraction.Domain;
using Microsoft.Data.Sqlite;

namespace ClinicalAbstraction.Storage;

public sealed record StoredDocument(string DocHash, string RawHash, string? PrintedId, string Path, string Text, int LineCount, long ByteSize);

public sealed record ExtractionRecord(string DocHash, string ExtractorVersion, string Model, string Status, string? DocumentStructure, int AssertionCount, int RejectedCount, string PatientKey = "unassigned");

public sealed record ModelCall(string Purpose, string Provider, string Model, long InputTokens, long OutputTokens, long CacheWriteTokens, long CacheReadTokens, long LatencyMs, string? Reference, string StopReason);

public sealed record ModelUsage(string Purpose, string Model, int Calls, long InputTokens, long OutputTokens, long CacheWriteTokens, long CacheReadTokens, long LatencyMs);

public sealed record PatientEncounterCount(string PatientKey, string? Name, int Encounters, int Days, string FirstDate, string LastDate);

/// <summary>One passage found by text search, with the question that led to it.</summary>
public sealed record GapRow(string Question, string? PrintedDocId, int Line, string Passage, string? SuggestedKind, string SearchTerms, string? PatientKey, string DocHash);

/// <summary>
/// The saved abstraction: one SQLite file. All SQL in the project lives in this class so every
/// query can be read in one place. One connection is shared and guarded by a lock, because SQLite
/// allows a single writer.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public string FilePath { get; }

    public Database(string filePath)
    {
        FilePath = filePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connection = new SqliteConnection($"Data Source={filePath}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        EnsureSchema();
    }

    private void EnsureSchema() => Execute("""
        CREATE TABLE IF NOT EXISTS documents (
            doc_hash     TEXT PRIMARY KEY,   -- SHA-256 of the text after normalising line endings and trailing spaces
            raw_hash     TEXT NOT NULL,      -- SHA-256 of the file bytes
            printed_id   TEXT,               -- the document ID as printed, filled in by extraction
            first_path   TEXT NOT NULL,
            text         TEXT NOT NULL,
            line_count   INTEGER NOT NULL,
            byte_size    INTEGER NOT NULL,
            first_seen   TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS document_copies (
            id        INTEGER PRIMARY KEY,
            doc_hash  TEXT NOT NULL,
            raw_hash  TEXT NOT NULL,
            path      TEXT NOT NULL,
            seen_at   TEXT NOT NULL,
            UNIQUE (doc_hash, path)
        );
        CREATE TABLE IF NOT EXISTS extractions (
            doc_hash            TEXT NOT NULL,
            extractor_version   TEXT NOT NULL,
            model               TEXT NOT NULL,
            status              TEXT NOT NULL,   -- complete, incomplete, truncated or failed
            patient_key         TEXT NOT NULL,   -- kept here so a failed document is still reported against its patient
            document_structure  TEXT,
            assertion_count     INTEGER NOT NULL,
            rejected_count      INTEGER NOT NULL,
            raw_json            TEXT NOT NULL,   -- the model's output exactly as returned
            created_at          TEXT NOT NULL,
            PRIMARY KEY (doc_hash, extractor_version)
        );
        CREATE TABLE IF NOT EXISTS assertions (
            id                 INTEGER PRIMARY KEY,
            doc_hash           TEXT NOT NULL,
            printed_doc_id     TEXT,
            patient_key        TEXT NOT NULL,
            encounter_id       TEXT,
            service_date       TEXT,
            kind               TEXT NOT NULL,
            line_start         INTEGER NOT NULL,
            line_end           INTEGER NOT NULL,
            quote              TEXT NOT NULL,
            quote_status       TEXT NOT NULL,
            record_type        TEXT NOT NULL,
            signed             TEXT NOT NULL,
            signer             TEXT,
            signed_at          TEXT,
            is_copy            INTEGER NOT NULL,
            extractor_version  TEXT NOT NULL,
            json               TEXT NOT NULL     -- the whole assertion, including kind-specific fields
        );
        CREATE INDEX IF NOT EXISTS ix_assertions_patient ON assertions (patient_key, kind);
        CREATE INDEX IF NOT EXISTS ix_assertions_doc ON assertions (doc_hash);
        CREATE TABLE IF NOT EXISTS rejected_assertions (
            id        INTEGER PRIMARY KEY,
            doc_hash  TEXT NOT NULL,
            reason    TEXT NOT NULL,
            kind      TEXT,
            critical  INTEGER NOT NULL,
            json      TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS patients (
            patient_key    TEXT PRIMARY KEY,
            mrn            TEXT,
            name           TEXT,
            date_of_birth  TEXT
        );
        CREATE TABLE IF NOT EXISTS patient_abstractions (
            patient_key  TEXT PRIMARY KEY,
            version      TEXT NOT NULL,     -- hash of the content, so it changes only when a conclusion changes
            json         TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS events (
            patient_key   TEXT NOT NULL,
            event_type    TEXT NOT NULL,
            event_key     TEXT NOT NULL,
            event_date    TEXT,
            category      TEXT,
            occurrence    TEXT,
            minutes_min   INTEGER,
            minutes_max   INTEGER,
            json          TEXT NOT NULL,
            PRIMARY KEY (patient_key, event_type, event_key)
        );
        CREATE TABLE IF NOT EXISTS weekly_results (
            patient_key   TEXT NOT NULL,
            week_start    TEXT NOT NULL,
            days_min      INTEGER NOT NULL,
            days_max      INTEGER NOT NULL,
            minutes_min   INTEGER NOT NULL,
            minutes_max   INTEGER NOT NULL,
            result        TEXT NOT NULL,
            partial_week  INTEGER NOT NULL,
            json          TEXT NOT NULL,
            PRIMARY KEY (patient_key, week_start)
        );
        CREATE TABLE IF NOT EXISTS model_calls (
            id             INTEGER PRIMARY KEY,
            purpose        TEXT NOT NULL,
            provider       TEXT NOT NULL,
            model          TEXT NOT NULL,
            input_tokens   INTEGER NOT NULL,   -- tokens billed at the full input price
            output_tokens  INTEGER NOT NULL,
            cache_write_tokens  INTEGER NOT NULL,
            cache_read_tokens   INTEGER NOT NULL,
            latency_ms     INTEGER NOT NULL,
            reference      TEXT,
            stop_reason    TEXT,
            created_at     TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS answers (
            id                   INTEGER PRIMARY KEY,
            question_id          TEXT,
            question             TEXT NOT NULL,
            abstraction_version  TEXT NOT NULL,
            model                TEXT NOT NULL,
            latency_ms           INTEGER NOT NULL,
            from_cache           INTEGER NOT NULL,
            markdown             TEXT NOT NULL,
            json                 TEXT NOT NULL,
            created_at           TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS gaps (
            id              INTEGER PRIMARY KEY,
            question        TEXT NOT NULL,
            patient_key     TEXT,
            doc_hash        TEXT NOT NULL,
            printed_doc_id  TEXT,
            line_number     INTEGER NOT NULL,
            passage         TEXT NOT NULL,
            search_terms    TEXT NOT NULL,
            suggested_kind  TEXT,
            created_at      TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS runs (
            id          INTEGER PRIMARY KEY,
            command     TEXT NOT NULL,
            started_at  TEXT NOT NULL,
            elapsed_ms  INTEGER NOT NULL,
            json        TEXT NOT NULL
        );
        """);

    // ---------- documents ----------

    public StoredDocument? FindDocument(string docHash)
    {
        lock (_gate)
        {
            using var command = Command("SELECT doc_hash, raw_hash, printed_id, first_path, text, line_count, byte_size FROM documents WHERE doc_hash = $h", ("$h", docHash));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDocument(reader) : null;
        }
    }

    public List<StoredDocument> ListDocuments()
    {
        lock (_gate)
        {
            using var command = Command("SELECT doc_hash, raw_hash, printed_id, first_path, text, line_count, byte_size FROM documents ORDER BY first_path");
            using var reader = command.ExecuteReader();
            var list = new List<StoredDocument>();
            while (reader.Read()) list.Add(ReadDocument(reader));
            return list;
        }
    }

    private static StoredDocument ReadDocument(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6));

    public void InsertDocument(StoredDocument d) => Execute(
        "INSERT INTO documents (doc_hash, raw_hash, printed_id, first_path, text, line_count, byte_size, first_seen) VALUES ($h, $r, $p, $path, $t, $l, $b, $now)",
        ("$h", d.DocHash), ("$r", d.RawHash), ("$p", d.PrintedId), ("$path", d.Path), ("$t", d.Text), ("$l", d.LineCount), ("$b", d.ByteSize), ("$now", Now()));

    public void SetPrintedId(string docHash, string? printedId) =>
        Execute("UPDATE documents SET printed_id = $p WHERE doc_hash = $h", ("$p", printedId), ("$h", docHash));

    /// <summary>Records that a copy of a known document was seen. Returns false if this path was already recorded.</summary>
    public bool RecordCopy(string docHash, string rawHash, string path) => Execute(
        "INSERT OR IGNORE INTO document_copies (doc_hash, raw_hash, path, seen_at) VALUES ($h, $r, $p, $now)",
        ("$h", docHash), ("$r", rawHash), ("$p", path), ("$now", Now())) > 0;

    public List<(string PrintedId, int Versions)> FindRevisedDocuments()
    {
        lock (_gate)
        {
            using var command = Command("SELECT printed_id, COUNT(*) FROM documents WHERE printed_id IS NOT NULL GROUP BY printed_id HAVING COUNT(*) > 1");
            using var reader = command.ExecuteReader();
            var list = new List<(string, int)>();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetInt32(1)));
            return list;
        }
    }

    // ---------- extraction ----------

    public ExtractionRecord? FindExtraction(string docHash, string extractorVersion)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT doc_hash, extractor_version, model, status, document_structure, assertion_count, rejected_count, patient_key FROM extractions WHERE doc_hash = $h AND extractor_version = $v",
                ("$h", docHash), ("$v", extractorVersion));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadExtraction(reader) : null;
        }
    }

    /// <summary>Documents whose extraction did not finish cleanly, so their patient's totals may be short.</summary>
    public List<ExtractionRecord> ListUnfinishedExtractions()
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT doc_hash, extractor_version, model, status, document_structure, assertion_count, rejected_count, patient_key FROM extractions WHERE status IN ('failed', 'truncated') ORDER BY doc_hash");
            using var reader = command.ExecuteReader();
            var list = new List<ExtractionRecord>();
            while (reader.Read()) list.Add(ReadExtraction(reader));
            return list;
        }
    }

    /// <summary>Every document's extraction, with its patient, its counts and when it was saved.</summary>
    public List<(string DocHash, string PatientKey, string Model, string Status, int Assertions, int Rejected, string CreatedAt)> ListExtractions()
    {
        lock (_gate)
        {
            using var command = Command("SELECT doc_hash, patient_key, model, status, assertion_count, rejected_count, created_at FROM extractions ORDER BY created_at, doc_hash");
            using var reader = command.ExecuteReader();
            var list = new List<(string, string, string, string, int, int, string)>();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6)));
            return list;
        }
    }

    private static ExtractionRecord ReadExtraction(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7));

    /// <summary>Saves one document's extraction in a single transaction, replacing any earlier result for the same version.</summary>
    public void SaveExtraction(ExtractionRecord record, string rawJson, List<Assertion> assertions, List<(string Reason, string? Kind, bool Critical, string Json)> rejected)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Run(transaction, "DELETE FROM assertions WHERE doc_hash = $h AND extractor_version = $v", ("$h", record.DocHash), ("$v", record.ExtractorVersion));
            Run(transaction, "DELETE FROM rejected_assertions WHERE doc_hash = $h", ("$h", record.DocHash));
            Run(transaction, "DELETE FROM extractions WHERE doc_hash = $h AND extractor_version = $v", ("$h", record.DocHash), ("$v", record.ExtractorVersion));
            Run(transaction,
                "INSERT INTO extractions (doc_hash, extractor_version, model, status, patient_key, document_structure, assertion_count, rejected_count, raw_json, created_at) VALUES ($h, $v, $m, $s, $pk, $d, $a, $r, $raw, $now)",
                ("$h", record.DocHash), ("$v", record.ExtractorVersion), ("$m", record.Model), ("$s", record.Status), ("$pk", record.PatientKey), ("$d", record.DocumentStructure),
                ("$a", record.AssertionCount), ("$r", record.RejectedCount), ("$raw", rawJson), ("$now", Now()));

            foreach (var a in assertions)
            {
                Run(transaction,
                    """
                    INSERT INTO assertions (doc_hash, printed_doc_id, patient_key, encounter_id, service_date, kind, line_start, line_end, quote, quote_status,
                                            record_type, signed, signer, signed_at, is_copy, extractor_version, json)
                    VALUES ($h, $p, $pk, $e, $d, $k, $ls, $le, $q, $qs, $rt, $s, $sr, $sa, $c, $v, '{}')
                    """,
                    ("$h", a.DocHash), ("$p", a.PrintedDocId), ("$pk", a.PatientKey), ("$e", a.EncounterId), ("$d", a.ServiceDate?.ToString("yyyy-MM-dd")),
                    ("$k", a.Kind), ("$ls", a.LineStart), ("$le", a.LineEnd), ("$q", a.Quote), ("$qs", a.QuoteStatus), ("$rt", a.RecordType),
                    ("$s", a.Signed), ("$sr", a.Signer), ("$sa", a.SignedAt?.ToString("s")), ("$c", a.IsCopy ? 1 : 0), ("$v", a.ExtractorVersion));
                a.Id = LastInsertId(transaction);
                Run(transaction, "UPDATE assertions SET json = $j WHERE id = $id", ("$j", Json.Write(a)), ("$id", a.Id));
            }

            foreach (var r in rejected)
                Run(transaction, "INSERT INTO rejected_assertions (doc_hash, reason, kind, critical, json) VALUES ($h, $r, $k, $c, $j)",
                    ("$h", record.DocHash), ("$r", r.Reason), ("$k", r.Kind), ("$c", r.Critical ? 1 : 0), ("$j", r.Json));

            transaction.Commit();
        }
    }

    public List<Assertion> GetAssertions(string? patientKey = null)
    {
        lock (_gate)
        {
            using var command = patientKey is null
                ? Command("SELECT json FROM assertions ORDER BY id")
                : Command("SELECT json FROM assertions WHERE patient_key = $p ORDER BY id", ("$p", patientKey));
            using var reader = command.ExecuteReader();
            var list = new List<Assertion>();
            while (reader.Read()) list.Add(Json.Read<Assertion>(reader.GetString(0)));
            return list;
        }
    }

    public List<(string DocHash, string Reason, string? Kind, bool Critical, string Json)> GetRejected()
    {
        lock (_gate)
        {
            using var command = Command("SELECT doc_hash, reason, kind, critical, json FROM rejected_assertions ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<(string, string, string?, bool, string)>();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3) == 1, reader.GetString(4)));
            return list;
        }
    }

    // ---------- patients and the saved abstraction ----------

    public void UpsertPatient(string patientKey, string? mrn, string? name, string? dateOfBirth) => Execute(
        """
        INSERT INTO patients (patient_key, mrn, name, date_of_birth) VALUES ($k, $m, $n, $d)
        ON CONFLICT (patient_key) DO UPDATE SET mrn = COALESCE(excluded.mrn, mrn), name = COALESCE(excluded.name, name), date_of_birth = COALESCE(excluded.date_of_birth, date_of_birth)
        """,
        ("$k", patientKey), ("$m", mrn), ("$n", name), ("$d", dateOfBirth));

    public List<(string PatientKey, string? Mrn, string? Name, string? DateOfBirth)> ListPatients()
    {
        lock (_gate)
        {
            using var command = Command("SELECT patient_key, mrn, name, date_of_birth FROM patients ORDER BY patient_key");
            using var reader = command.ExecuteReader();
            var list = new List<(string, string?, string?, string?)>();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            return list;
        }
    }

    public void SaveAbstraction(PatientAbstraction abstraction, IEnumerable<(string WeekStart, int DaysMin, int DaysMax, int MinutesMin, int MinutesMax, string Result, bool Partial, string Json)> weeks)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Run(transaction, "DELETE FROM events WHERE patient_key = $p", ("$p", abstraction.PatientKey));
            Run(transaction, "DELETE FROM weekly_results WHERE patient_key = $p", ("$p", abstraction.PatientKey));
            Run(transaction,
                """
                INSERT INTO patient_abstractions (patient_key, version, json, updated_at) VALUES ($p, $v, $j, $now)
                ON CONFLICT (patient_key) DO UPDATE SET version = excluded.version, json = excluded.json, updated_at = excluded.updated_at
                """,
                ("$p", abstraction.PatientKey), ("$v", abstraction.Version), ("$j", Json.Write(abstraction)), ("$now", Now()));

            foreach (var e in abstraction.Encounters)
                Run(transaction,
                    "INSERT OR REPLACE INTO events (patient_key, event_type, event_key, event_date, category, occurrence, minutes_min, minutes_max, json) VALUES ($p, 'encounter', $k, $d, $c, $o, $min, $max, $j)",
                    ("$p", abstraction.PatientKey), ("$k", e.EventKey), ("$d", e.ServiceDate?.ToString("yyyy-MM-dd")), ("$c", e.Category.Value), ("$o", e.Occurrence),
                    ("$min", e.MinutesMin), ("$max", e.MinutesMax), ("$j", Json.Write(e)));

            foreach (var m in abstraction.Measurements)
                Run(transaction,
                    "INSERT OR REPLACE INTO events (patient_key, event_type, event_key, event_date, category, occurrence, minutes_min, minutes_max, json) VALUES ($p, 'measurement', $k, $d, $c, NULL, NULL, NULL, $j)",
                    ("$p", abstraction.PatientKey), ("$k", $"{m.Instrument}|{m.CompletedOn:yyyy-MM-dd}"), ("$d", m.CompletedOn?.ToString("yyyy-MM-dd")), ("$c", m.Instrument), ("$j", Json.Write(m)));

            foreach (var plan in abstraction.Plans)
                Run(transaction,
                    "INSERT OR REPLACE INTO events (patient_key, event_type, event_key, event_date, category, occurrence, minutes_min, minutes_max, json) VALUES ($p, 'plan_version', $k, $d, NULL, NULL, NULL, NULL, $j)",
                    ("$p", abstraction.PatientKey), ("$k", $"plan|{plan.EffectiveFrom:yyyy-MM-dd}"), ("$d", plan.EffectiveFrom?.ToString("yyyy-MM-dd")), ("$j", Json.Write(plan)));

            foreach (var w in weeks)
                Run(transaction,
                    "INSERT INTO weekly_results (patient_key, week_start, days_min, days_max, minutes_min, minutes_max, result, partial_week, json) VALUES ($p, $w, $d1, $d2, $m1, $m2, $r, $pw, $j)",
                    ("$p", abstraction.PatientKey), ("$w", w.WeekStart), ("$d1", w.DaysMin), ("$d2", w.DaysMax), ("$m1", w.MinutesMin), ("$m2", w.MinutesMax),
                    ("$r", w.Result), ("$pw", w.Partial ? 1 : 0), ("$j", w.Json));

            transaction.Commit();
        }
    }

    public PatientAbstraction? LoadAbstraction(string patientKey)
    {
        lock (_gate)
        {
            using var command = Command("SELECT json FROM patient_abstractions WHERE patient_key = $p", ("$p", patientKey));
            var json = command.ExecuteScalar() as string;
            return json is null ? null : Json.Read<PatientAbstraction>(json);
        }
    }

    public List<PatientAbstraction> LoadAllAbstractions()
    {
        lock (_gate)
        {
            using var command = Command("SELECT json FROM patient_abstractions ORDER BY patient_key");
            using var reader = command.ExecuteReader();
            var list = new List<PatientAbstraction>();
            while (reader.Read()) list.Add(Json.Read<PatientAbstraction>(reader.GetString(0)));
            return list;
        }
    }

    /// <summary>One version for the whole collection, built from every patient's version.</summary>
    public string CollectionVersion()
    {
        lock (_gate)
        {
            using var command = Command("SELECT patient_key || ':' || version FROM patient_abstractions ORDER BY patient_key");
            using var reader = command.ExecuteReader();
            var parts = new List<string>();
            while (reader.Read()) parts.Add(reader.GetString(0));
            return Hashing.Sha256(string.Join("|", parts))[..16];
        }
    }

    public List<(string PatientKey, string WeekStart, string Json)> LoadWeeklyResults()
    {
        lock (_gate)
        {
            using var command = Command("SELECT patient_key, week_start, json FROM weekly_results ORDER BY patient_key, week_start");
            using var reader = command.ExecuteReader();
            var list = new List<(string, string, string)>();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return list;
        }
    }

    /// <summary>
    /// Patients who have at least one encounter dated inside a period, with how many encounters and
    /// on how many days. It reads the events table only, so deciding who a question covers never
    /// loads a patient's full abstraction. A date left out leaves that end of the period open.
    /// </summary>
    public List<PatientEncounterCount> ListPatientsWithEncounters(string? from, string? to)
    {
        lock (_gate)
        {
            using var command = Command(
                """
                SELECT e.patient_key, p.name, COUNT(*), COUNT(DISTINCT e.event_date), MIN(e.event_date), MAX(e.event_date)
                FROM events e LEFT JOIN patients p ON p.patient_key = e.patient_key
                WHERE e.event_type = 'encounter' AND e.event_date IS NOT NULL
                  AND ($from IS NULL OR e.event_date >= $from)
                  AND ($to IS NULL OR e.event_date <= $to)
                GROUP BY e.patient_key, p.name
                ORDER BY e.patient_key
                """,
                ("$from", from), ("$to", to));
            using var reader = command.ExecuteReader();
            var list = new List<PatientEncounterCount>();
            while (reader.Read())
                list.Add(new PatientEncounterCount(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
            return list;
        }
    }

    // ---------- model calls, answers, gaps, runs ----------

    public void RecordModelCall(ModelCall call) => Execute(
        "INSERT INTO model_calls (purpose, provider, model, input_tokens, output_tokens, cache_write_tokens, cache_read_tokens, latency_ms, reference, stop_reason, created_at) VALUES ($p, $pr, $m, $i, $o, $cw, $cr, $l, $r, $s, $now)",
        ("$p", call.Purpose), ("$pr", call.Provider), ("$m", call.Model), ("$i", call.InputTokens), ("$o", call.OutputTokens),
        ("$cw", call.CacheWriteTokens), ("$cr", call.CacheReadTokens), ("$l", call.LatencyMs),
        ("$r", call.Reference), ("$s", call.StopReason), ("$now", Now()));

    public List<ModelUsage> SummariseModelCalls()
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT purpose, model, COUNT(*), SUM(input_tokens), SUM(output_tokens), SUM(cache_write_tokens), SUM(cache_read_tokens), SUM(latency_ms) FROM model_calls GROUP BY purpose, model ORDER BY purpose, model");
            using var reader = command.ExecuteReader();
            var list = new List<ModelUsage>();
            while (reader.Read())
                list.Add(new ModelUsage(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
            return list;
        }
    }

    /// <summary>Every model call one at a time, with what it was for and when, so usage can be tied to one ingest and one patient's documents.</summary>
    public List<(ModelCall Call, string CreatedAt)> ListModelCalls()
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT purpose, provider, model, input_tokens, output_tokens, cache_write_tokens, cache_read_tokens, latency_ms, reference, stop_reason, created_at FROM model_calls ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<(ModelCall, string)>();
            while (reader.Read())
                list.Add((new ModelCall(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                    reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? "" : reader.GetString(9)), reader.GetString(10)));
            return list;
        }
    }

    public (string Markdown, string Json)? FindCachedAnswer(string question, string abstractionVersion, string model)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT markdown, json FROM answers WHERE question = $q AND abstraction_version = $v AND model = $m AND from_cache = 0 ORDER BY id DESC LIMIT 1",
                ("$q", question), ("$v", abstractionVersion), ("$m", model));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
        }
    }

    /// <summary>Saves an answer and returns the row it was saved as.</summary>
    public long SaveAnswer(string? questionId, string question, string abstractionVersion, string model, long latencyMs, bool fromCache, string markdown, string json)
    {
        lock (_gate)
        {
            using var command = Command(
                "INSERT INTO answers (question_id, question, abstraction_version, model, latency_ms, from_cache, markdown, json, created_at) VALUES ($id, $q, $v, $m, $l, $c, $md, $j, $now) RETURNING id",
                ("$id", questionId), ("$q", question), ("$v", abstractionVersion), ("$m", model), ("$l", latencyMs), ("$c", fromCache ? 1 : 0), ("$md", markdown), ("$j", json), ("$now", Now()));
            return (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>The most recent answers first, without their text, for the review page's list of earlier questions.</summary>
    public List<(long Id, string? QuestionId, string Question, long LatencyMs, bool FromCache, string CreatedAt)> ListRecentAnswers(int limit)
    {
        lock (_gate)
        {
            using var command = Command("SELECT id, question_id, question, latency_ms, from_cache, created_at FROM answers ORDER BY id DESC LIMIT $n", ("$n", limit));
            using var reader = command.ExecuteReader();
            var list = new List<(long, string?, string, long, bool, string)>();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4) == 1, reader.GetString(5)));
            return list;
        }
    }

    public (long Id, string Question, string Model, long LatencyMs, bool FromCache, string Markdown, string Json, string CreatedAt)? FindAnswer(long id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT id, question, model, latency_ms, from_cache, markdown, json, created_at FROM answers WHERE id = $id", ("$id", id));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4) == 1, reader.GetString(5), reader.GetString(6), reader.GetString(7))
                : null;
        }
    }

    public int CountAnswers()
    {
        lock (_gate)
        {
            using var command = Command("SELECT COUNT(*) FROM answers");
            return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
        }
    }

    public List<(string? QuestionId, string Question, long LatencyMs, bool FromCache, string CreatedAt)> ListAnswers()
    {
        lock (_gate)
        {
            using var command = Command("SELECT question_id, question, latency_ms, from_cache, created_at FROM answers ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<(string?, string, long, bool, string)>();
            while (reader.Read()) list.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3) == 1, reader.GetString(4)));
            return list;
        }
    }

    public void RecordGap(string question, string? patientKey, string docHash, string? printedDocId, int lineNumber, string passage, string searchTerms, string? suggestedKind) => Execute(
        "INSERT INTO gaps (question, patient_key, doc_hash, printed_doc_id, line_number, passage, search_terms, suggested_kind, created_at) VALUES ($q, $p, $h, $d, $l, $t, $s, $k, $now)",
        ("$q", question), ("$p", patientKey), ("$h", docHash), ("$d", printedDocId), ("$l", lineNumber), ("$t", passage), ("$s", searchTerms), ("$k", suggestedKind), ("$now", Now()));

    public List<GapRow> ListGaps()
    {
        lock (_gate)
        {
            using var command = Command("SELECT question, printed_doc_id, line_number, passage, suggested_kind, search_terms, patient_key, doc_hash FROM gaps ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<GapRow>();
            while (reader.Read())
                list.Add(new GapRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7)));
            return list;
        }
    }

    /// <summary>Every path a document's content has been seen at, in the order they were seen.</summary>
    public List<string> ListCopies(string docHash)
    {
        lock (_gate)
        {
            using var command = Command("SELECT path FROM document_copies WHERE doc_hash = $h ORDER BY id", ("$h", docHash));
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    public void RecordRun(string command, DateTime startedAt, long elapsedMs, string json) => Execute(
        "INSERT INTO runs (command, started_at, elapsed_ms, json) VALUES ($c, $s, $e, $j)",
        ("$c", command), ("$s", startedAt.ToString("s")), ("$e", elapsedMs), ("$j", json));

    public List<(string Command, string StartedAt, long ElapsedMs, string Json)> ListRuns()
    {
        lock (_gate)
        {
            using var command = Command("SELECT command, started_at, elapsed_ms, json FROM runs ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<(string, string, long, string)>();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
            return list;
        }
    }

    public Dictionary<string, long> CountRows()
    {
        var counts = new Dictionary<string, long>();
        foreach (var table in new[] { "documents", "document_copies", "extractions", "assertions", "rejected_assertions", "patients", "events", "weekly_results", "model_calls", "answers", "gaps" })
        {
            lock (_gate)
            {
                using var command = Command($"SELECT COUNT(*) FROM {table}");
                counts[table] = (long)(command.ExecuteScalar() ?? 0L);
            }
        }
        return counts;
    }

    /// <summary>Folds the write-ahead log into the main file so the file size on disk is the whole abstraction.</summary>
    public void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE);");

    // ---------- helpers ----------

    private static string Now() => DateTime.UtcNow.ToString("s") + "Z";

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    private void Run(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private long LastInsertId(SqliteTransaction transaction)
    {
        using var command = Command("SELECT last_insert_rowid()");
        command.Transaction = transaction;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void Dispose() => _connection.Dispose();
}
