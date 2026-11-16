namespace A3ASD;

// 레코드 저장소 추상화. 운영 = SqlRecordStore(MSSQL), 자체 테스트 = InMemoryStore.
// Record store abstraction. Production = SqlRecordStore(MSSQL), self-test = InMemoryStore.
// H3DB 레코드는 다중 컬럼: c_id, c_sheadera~c, c_headera~c, d_cdate, d_udate, c_status, m_body.
// An H3DB record is multi-column: c_id, c_sheadera~c, c_headera~c, d_cdate, d_udate, c_status, m_body.
// DTO 는 전부 readonly struct — 요청마다 배열·박싱 없이 값으로 오간다.
// All DTOs are readonly structs: no per-request arrays or boxing.
// 비동기 반환은 Task 를 유지한다: SqlClient 가 Task 를 돌려주므로 ValueTask 로 감싸도 할당이 줄지 않고,
// "한 번만 await" 제약만 생긴다. ValueTask 는 요청마다 도는 내부 경로(Protocol/PayServer)에만 쓴다.
// Return types stay Task: SqlClient already returns Task, so wrapping in ValueTask saves nothing and adds the
// single-await constraint. ValueTask is used only on the per-request internal paths (Protocol/PayServer).

// 로드 포맷 / load formats: Full = 2011→3011 (charac/storage, 11컬럼), Account = 2021→3012 (9컬럼, m_body·d_cdate 없음)
public enum LoadFormat { Full, Account }

// 저장/생성 와이어의 8개 쓰기 컬럼(c_id·날짜 제외). 순서 = 와이어 순서 = SaveColumns 순서.
// The 8 writable columns of the save/create wire (excluding c_id and dates). Order = wire order = SaveColumns.
public readonly record struct RecordFields(
    string? SHeaderA, string? SHeaderB, string? SHeaderC,
    string? HeaderA, string? HeaderB, string? HeaderC,
    string? Status, string? Body)
{
    public const int Count = 8;
    public static readonly string[] SaveColumns =
        { "c_sheadera", "c_sheaderb", "c_sheaderc", "c_headera", "c_headerb", "c_headerc", "c_status", "m_body" };

    // i 번째 컬럼 값(SaveColumns 순서) / value of the i-th column in SaveColumns order
    public string? Get(int i) => i switch
    {
        0 => SHeaderA, 1 => SHeaderB, 2 => SHeaderC,
        3 => HeaderA, 4 => HeaderB, 5 => HeaderC,
        6 => Status, 7 => Body,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };
}

// 로드 결과. Account 포맷에서는 CDate·Fields.Body 가 null(조회 안 함).
// Load result. In the Account format CDate and Fields.Body are null (not selected).
public readonly record struct LoadedRecord(string? CId, RecordFields Fields, DateTime? CDate, DateTime? UDate);

// 계정 만료 조회 결과. ColumnExists=false 면 컬럼이 없는 DB, Expire=null 이면 제한 없음.
// Expiry lookup result. ColumnExists=false: column absent. Expire=null: no limit.
public readonly record struct PayExpiry(bool ColumnExists, DateTime? Expire, DateTime Now);

public interface IRecordStore
{
    // c_id 로 읽는다. 없으면 null. / Load by c_id; null if no row.
    Task<LoadedRecord?> LoadAsync(LoadFormat format, string cId);

    // 저장(2071): UPDATE 전용, 없으면 INSERT 하지 않음(원본 동일). d_udate = getdate(). 갱신 행 수 반환.
    // Save (2071): UPDATE only, never INSERT (as original). d_udate = getdate(). Returns rows updated.
    Task<int> UpdateAsync(RecordFields fields, string cId);

    // 신규 삽입(이미 있으면 false). 생성(2002)용. d_cdate = getdate(), d_udate NULL(원본 동일).
    // Insert new (false if exists). For create (2002). d_cdate = getdate(), d_udate NULL (as original).
    Task<bool> InsertNewAsync(RecordFields fields, string cId);

    Task<bool> ExistsAsync(string cId);   // 존재 확인 / existence check
    // 단일 컬럼 갱신(d_udate 미변경, 원본 동일). 갱신 행 수 반환. 행 DELETE 는 의도적으로 제공 안 함(원본 2048 은 soft delete).
    // Update one column (d_udate untouched, as original). Returns rows. Row DELETE intentionally not offered.
    Task<int> UpdateColumnAsync(string column, string? value, string cId);
    // 만료 시각 조회. 행이 없거나 NULL 이면 Expire=null, 컬럼이 없으면 ColumnExists=false.
    // Expiry lookup. No row or NULL: Expire=null. Missing column: ColumnExists=false.
    Task<PayExpiry> LoadExpiryAsync(string column, string cId);
    Task PingAsync();                     // 연결 점검 / connection check
}
