using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace A3ASD;

// MSSQL 레코드 저장소. 테이블은 ASD 공통 컬럼 보유: c_id, c_sheadera~c, c_headera~c, d_cdate, d_udate, c_status, m_body.
// MSSQL record store. Tables carry the ASD common columns above.
// SQL 문은 생성자에서 1회 조립한다(테이블·컬럼은 설정/상수라 요청마다 다시 만들 이유가 없다).
// SQL text is built once in the constructor; table/columns are config/constants, never per request.
// 테이블명·컬럼명은 설정/내부 상수에서만 오므로(사용자 입력 아님) 식별자 화이트리스트로 검증 후 삽입.
// Table/column names come from config/constants only (not user input); validated as identifiers before use.
// SequentialAccess 는 쓰지 않는다: m_body 는 varchar(8000)/text 이고 실제 데이터는 수 KB 수준이라
// 행 전체가 패킷 1~2개로 온다. 스트리밍은 수백 KB LOB 에서만 이득이고 여기서는 컬럼별 async 읽기 비용만 는다.
// No SequentialAccess: m_body is varchar(8000)/text holding a few KB, so the whole row arrives in 1-2 packets;
// streaming only pays off for LOBs of hundreds of KB and would add per-column async overhead here.
public sealed class SqlRecordStore : IRecordStore
{
    private readonly string _conn;   // 연결 문자열 / connection string
    private readonly string _table;  // 검증된 테이블명 / validated table name
    private readonly string _selectFull, _selectAccount, _update, _insert;

    // @v0..@v7 = RecordFields.SaveColumns 순서 / parameter names in SaveColumns order
    private static readonly string[] ParamNames = Enumerable.Range(0, RecordFields.Count).Select(i => "@v" + i).ToArray();

    public SqlRecordStore(string connectionString, string table)
    {
        _conn = connectionString;
        _table = ValidateIdent(table);
        foreach (var c in RecordFields.SaveColumns) ValidateIdent(c);

        // 로드 2종: full(2011) = 11컬럼, account(2021) = 9컬럼(d_cdate·m_body 없음). 순서는 LoadAsync 의 ordinal 과 같다.
        // Two load shapes: full(2011) = 11 columns, account(2021) = 9 (no d_cdate, m_body). Order matches LoadAsync ordinals.
        const string head = "[c_id], [c_sheadera], [c_sheaderb], [c_sheaderc], [c_headera], [c_headerb], [c_headerc]";
        _selectFull = $"SELECT {head}, [d_cdate], [d_udate], [c_status], [m_body] FROM [{_table}] WHERE c_id = @id";
        _selectAccount = $"SELECT {head}, [d_udate], [c_status] FROM [{_table}] WHERE c_id = @id";

        string setList = string.Join(", ", RecordFields.SaveColumns.Select((c, i) => $"[{c}] = {ParamNames[i]}"));
        string insCols = string.Join(", ", RecordFields.SaveColumns.Select(c => $"[{c}]"));
        string insVals = string.Join(", ", ParamNames);
        // 저장(2071): UPDATE 전용, d_udate = getdate(), d_cdate 보존 / save: UPDATE only, d_udate = getdate()
        _update = $"UPDATE [{_table}] SET {setList}, d_udate = getdate() WHERE c_id = @id;";
        // 생성(2002): 날짜는 d_cdate 만 기록 → d_udate NULL(로드 시 제로날짜).
        // Create (2002): only d_cdate is set -> d_udate NULL (zero date on load).
        _insert = $"IF NOT EXISTS (SELECT 1 FROM [{_table}] WHERE c_id = @id) " +
                  $"INSERT INTO [{_table}] (c_id, {insCols}, d_cdate) VALUES (@id, {insVals}, getdate());";
    }

    // 식별자 검증(영숫자·밑줄만) / validate identifier (alnum + underscore only)
    private static string ValidateIdent(string name)
    {
        bool ok = !string.IsNullOrEmpty(name) && name.Length <= 64;
        foreach (char c in name.AsSpan())
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) { ok = false; break; }
        if (!ok) throw new ArgumentException($"invalid identifier: {name}");
        return name;
    }

    public async Task<LoadedRecord?> LoadAsync(LoadFormat format, string cId)
    {
        bool full = format == LoadFormat.Full;
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(full ? _selectFull : _selectAccount, cn);
        cmd.Parameters.AddWithValue("@id", cId);
        // SingleRow: c_id 조회는 1행. 행 전체가 버퍼링되므로 아래 동기 접근자는 네트워크 대기 없이 끝난다.
        // SingleRow: one row per c_id. The row is fully buffered, so the sync accessors below never block on the network.
        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await rd.ReadAsync()) return null;   // 레코드 없음 / no record

        int i = 0;
        string? id = Text(rd, i++);
        var f = new RecordFields(
            Text(rd, i++), Text(rd, i++), Text(rd, i++),
            Text(rd, i++), Text(rd, i++), Text(rd, i++),
            Status: null, Body: null);
        DateTime? cdate = full ? Date(rd, i++) : null;
        DateTime? udate = Date(rd, i++);
        string? status = Text(rd, i++);
        string? body = full ? Text(rd, i++) : null;
        return new LoadedRecord(id, f with { Status = status, Body = body }, cdate, udate);
    }

    // 문자열 컬럼. char/nchar 고정 길이는 뒤 공백 제거(BDE FixedChar 와 같은 동작, 예: char 형 c_id).
    // varchar 는 그대로. 문자열이 아닌 값은 Invariant 문자열화.
    // Text column. Fixed-length char/nchar: trim trailing spaces (as BDE FixedChar). Non-strings -> invariant ToString.
    private static string? Text(SqlDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return null;
        object v = rd.GetValue(i);
        if (v is not string s) return Convert.ToString(v, CultureInfo.InvariantCulture);
        string t = rd.GetDataTypeName(i);
        return t.Equals("char", StringComparison.OrdinalIgnoreCase) || t.Equals("nchar", StringComparison.OrdinalIgnoreCase)
            ? s.TrimEnd(' ') : s;
    }

    // 날짜 컬럼. DateTime 이 아니면 null(→ 제로날짜) / date column; non-DateTime -> null (zero date)
    private static DateTime? Date(SqlDataReader rd, int i) =>
        !rd.IsDBNull(i) && rd.GetValue(i) is DateTime d ? d : null;

    public async Task<int> UpdateAsync(RecordFields fields, string cId)
    {
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = Command(cn, _update, fields, cId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> InsertNewAsync(RecordFields fields, string cId)
    {
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = Command(cn, _insert, fields, cId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static SqlCommand Command(SqlConnection cn, string sql, in RecordFields fields, string cId)
    {
        var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@id", cId);
        for (int i = 0; i < RecordFields.Count; i++)
            cmd.Parameters.AddWithValue(ParamNames[i], (object?)fields.Get(i) ?? DBNull.Value);
        return cmd;
    }

    // 컬럼이 없으면 SQL 오류 207. 컬럼을 나중에 추가해도 재시작 없이 바로 적용되도록 매번 조회한다.
    // Missing column raises SQL error 207. Queried every time so a column added later works without a restart.
    public async Task<PayExpiry> LoadExpiryAsync(string column, string cId)
    {
        ValidateIdent(column);
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT [{column}], getdate() FROM [{_table}] WHERE c_id = @id", cn);
        cmd.Parameters.AddWithValue("@id", cId);
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
            if (!await rd.ReadAsync()) return new PayExpiry(true, null, DateTime.Now);
            DateTime? expire = rd.IsDBNull(0) ? null : rd.GetDateTime(0);
            return new PayExpiry(true, expire, rd.GetDateTime(1));
        }
        catch (SqlException ex) when (ex.Number == 207)
        {
            return new PayExpiry(false, null, DateTime.Now);
        }
    }

    public async Task<bool> ExistsAsync(string cId)
    {
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{_table}] WHERE c_id = @id", cn);
        cmd.Parameters.AddWithValue("@id", cId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    public async Task<int> UpdateColumnAsync(string column, string? value, string cId)
    {
        ValidateIdent(column);
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand($"UPDATE [{_table}] SET [{column}] = @v WHERE c_id = @id", cn);
        cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", cId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task PingAsync()
    {
        await using var cn = new SqlConnection(_conn);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT 1", cn);
        await cmd.ExecuteScalarAsync();
    }
}
