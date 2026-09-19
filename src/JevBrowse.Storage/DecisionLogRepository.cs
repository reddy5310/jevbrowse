namespace JevBrowse.Storage;

/// <summary>Explainability log (§11, M7). Stores what was decided and by whom; never the full input text.</summary>
public sealed class DecisionLogRepository
{
    public sealed record Row(long Id, DateTimeOffset At, string Task, string Source, string Rule, string? Model, string DataClass, bool Redacted, int RedactionCount, int InputChars, string OutputPreview, string Version);

    private readonly BrowserDb _db;
    public DecisionLogRepository(BrowserDb db) => _db = db;

    public void Append(DateTimeOffset at, string task, string source, string rule, string? model, string dataClass, bool redacted, int redactionCount, int inputChars, string output, string version)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decision_log (at, task, source, rule, model, data_class, redacted, redaction_count, input_chars, output_preview, version)
            VALUES ($at, $task, $src, $rule, $model, $dc, $red, $rc, $ic, $out, $v)
            """;
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$task", task);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$rule", rule);
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dc", dataClass);
        cmd.Parameters.AddWithValue("$red", redacted ? 1 : 0);
        cmd.Parameters.AddWithValue("$rc", redactionCount);
        cmd.Parameters.AddWithValue("$ic", inputChars);
        cmd.Parameters.AddWithValue("$out", output.Length > 200 ? output[..200] : output);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Row> Recent(int limit = 100)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, at, task, source, rule, model, data_class, redacted, redaction_count, input_chars, output_preview, version FROM decision_log ORDER BY at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Row>();
        while (r.Read())
            list.Add(new Row(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt32(7) != 0, r.GetInt32(8), r.GetInt32(9), r.GetString(10), r.GetString(11)));
        return list;
    }

    /// <summary>Count of cloud invocations per data class: the privacy success metric in Table A.15.</summary>
    public IReadOnlyDictionary<string, int> CloudCallsByClass()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT data_class, COUNT(*) FROM decision_log WHERE rule LIKE 'provider:%:cloud' GROUP BY data_class";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, int>();
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }
}
