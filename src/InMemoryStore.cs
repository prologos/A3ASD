using System.Collections.Concurrent;

namespace A3ASD;

// 인메모리 저장소 (DB 없이 프로토콜 검증용). H3DB.ini [db] mode=memory 일 때 사용.
// In-memory store (verify protocol without a DB). Used when H3DB.ini [db] mode=memory.
// 레코드 = 컬럼명 -> 값(string 또는 DateTime). 날짜는 DateTime.Now 로 getdate() 흉내.
// Record = column name -> value (string or DateTime). Dates mimic getdate() via DateTime.Now.
// 시험용이라 할당 최적화 대상이 아니다 / test-only; not an allocation target.
public sealed class InMemoryStore : IRecordStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _map = new();

    public Task<LoadedRecord?> LoadAsync(LoadFormat format, string cId)
    {
        if (!_map.TryGetValue(cId, out var rec)) return Task.FromResult<LoadedRecord?>(null);
        lock (rec)
        {
            string? S(string col) => rec.TryGetValue(col, out var v) ? v as string : null;
            DateTime? D(string col) => rec.TryGetValue(col, out var v) && v is DateTime d ? d : null;
            bool full = format == LoadFormat.Full;
            var f = new RecordFields(S("c_sheadera"), S("c_sheaderb"), S("c_sheaderc"),
                                     S("c_headera"), S("c_headerb"), S("c_headerc"),
                                     S("c_status"), full ? S("m_body") : null);
            return Task.FromResult<LoadedRecord?>(new LoadedRecord(S("c_id"), f, full ? D("d_cdate") : null, D("d_udate")));
        }
    }

    // UPDATE 전용(없으면 0) / UPDATE only (0 if missing)
    public Task<int> UpdateAsync(RecordFields fields, string cId)
    {
        if (!_map.TryGetValue(cId, out var rec)) return Task.FromResult(0);
        lock (rec)
        {
            for (int i = 0; i < RecordFields.Count; i++) rec[RecordFields.SaveColumns[i]] = fields.Get(i);
            rec["d_udate"] = DateTime.Now;   // getdate() 흉내 / mimic getdate()
        }
        return Task.FromResult(1);
    }

    public Task<bool> InsertNewAsync(RecordFields fields, string cId)
    {
        var rec = new Dictionary<string, object?> { ["c_id"] = cId };
        for (int i = 0; i < RecordFields.Count; i++) rec[RecordFields.SaveColumns[i]] = fields.Get(i);
        rec["d_cdate"] = DateTime.Now; rec["d_udate"] = null;   // 원본 INSERT 는 d_udate 미기입 / original leaves d_udate NULL
        return Task.FromResult(_map.TryAdd(cId, rec));
    }

    public Task<bool> ExistsAsync(string cId) => Task.FromResult(_map.ContainsKey(cId));

    // 단일 컬럼 갱신(d_udate 미변경) / update one column (d_udate untouched)
    public Task<int> UpdateColumnAsync(string column, string? value, string cId)
    {
        if (!_map.TryGetValue(cId, out var rec)) return Task.FromResult(0);
        lock (rec) rec[column] = value;
        return Task.FromResult(1);
    }

    // 만료 조회. 저장 와이어로는 문자열만 들어오므로 yyyyMMdd.HHmmss 를 날짜로 해석한다(자체 테스트용).
    // Expiry lookup. The save wire only carries strings, so yyyyMMdd.HHmmss is parsed as a date (self-test).
    public Task<PayExpiry> LoadExpiryAsync(string column, string cId)
    {
        DateTime? expire = null;
        if (_map.TryGetValue(cId, out var rec))
        {
            object? v;
            lock (rec) rec.TryGetValue(column, out v);
            if (v is DateTime d) expire = d;
            else if (DateTime.TryParseExact(v as string, "yyyyMMdd.HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out var parsed)) expire = parsed;
        }
        return Task.FromResult(new PayExpiry(true, expire, DateTime.Now));
    }
    public Task PingAsync() => Task.CompletedTask;
}
