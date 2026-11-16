-- A3ASD: c_id 조회용 인덱스 생성 / c_id lookup indexes for the ASD tables
--
-- 배경: ASD 의 모든 요청(2011/2021/2071/2002/2048, Pay 만료 확인)은 WHERE c_id = @id 다.
--   c_id 에 인덱스가 없으면 요청마다 테이블 스캔이 된다.
-- Background: every ASD request filters on c_id; without an index each one is a table scan.
--
-- 대상 / Targets:
--   account      PK 가 id 라 c_id 인덱스가 없음 -> UNIQUE 인덱스 생성
--   ItemStorage0 힙(인덱스 없음)               -> UNIQUE 인덱스 생성
--   charac0      보통 PK(c_id) 가 이미 있음     -> c_id 키 인덱스가 하나도 없을 때만 비고유 인덱스 생성
--
-- 주의 / Notes:
--   * 실행 전에 0단계 점검 결과를 본다. dup_ids 가 1 이상이면 UNIQUE 생성이 실패하므로
--     아래 "비고유 대안" 을 쓰거나 중복을 먼저 정리한다.
--   * Standard Edition 은 ONLINE 인덱스 생성이 안 된다. 수백 KB~수 MB 테이블이면 1초 미만이지만
--     그동안 쓰기가 잠기므로 게임 서버가 한가할 때 실행한다.
--   * 전부 IF NOT EXISTS 로 감싸 여러 번 실행해도 안전하다. A3ASD 코드 변경은 없다.
--   * 시험 DB 에서 먼저 실행한다. / Run on a test DB first.

USE A3;
GO

------------------------------------------------------------------------------
-- 0. 사전 점검 (읽기 전용) / pre-check (read-only)
--    dup_ids 가 0 이어야 UNIQUE 생성이 성공한다.
------------------------------------------------------------------------------
SELECT 'account'      AS tbl, COUNT(*) AS rows_, COUNT(DISTINCT c_id) AS distinct_ids,
       (SELECT COUNT(*) FROM (SELECT c_id FROM dbo.account      GROUP BY c_id HAVING COUNT(*) > 1) d) AS dup_ids FROM dbo.account
UNION ALL
SELECT 'charac0',      COUNT(*), COUNT(DISTINCT c_id),
       (SELECT COUNT(*) FROM (SELECT c_id FROM dbo.charac0      GROUP BY c_id HAVING COUNT(*) > 1) d) FROM dbo.charac0
UNION ALL
SELECT 'ItemStorage0', COUNT(*), COUNT(DISTINCT c_id),
       (SELECT COUNT(*) FROM (SELECT c_id FROM dbo.ItemStorage0 GROUP BY c_id HAVING COUNT(*) > 1) d) FROM dbo.ItemStorage0;

-- 현재 인덱스 / current indexes
SELECT o.name AS tbl, ISNULL(i.name, '(heap)') AS idx, i.type_desc, i.is_unique, i.is_primary_key,
       STUFF((SELECT ',' + c.name FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
              ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, '') AS key_cols
FROM sys.objects o JOIN sys.indexes i ON i.object_id = o.object_id
WHERE o.name IN ('account', 'charac0', 'ItemStorage0')
ORDER BY o.name, i.index_id;
GO

------------------------------------------------------------------------------
-- 1. account.c_id  (PK 는 id 라서 c_id 조회가 스캔) / PK is on id, so c_id lookups scan
------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.account') AND name = 'IX_account_c_id')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX IX_account_c_id ON dbo.account (c_id);
    PRINT 'created IX_account_c_id';
END
ELSE PRINT 'IX_account_c_id already exists';
GO

------------------------------------------------------------------------------
-- 2. ItemStorage0.c_id  (힙, 인덱스 없음) / heap with no index
------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ItemStorage0') AND name = 'IX_ItemStorage0_c_id')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX IX_ItemStorage0_c_id ON dbo.ItemStorage0 (c_id);
    PRINT 'created IX_ItemStorage0_c_id';
END
ELSE PRINT 'IX_ItemStorage0_c_id already exists';
GO

------------------------------------------------------------------------------
-- 3. charac0.c_id  (c_id 를 첫 키로 하는 인덱스가 하나도 없을 때만, 중복 가능성 때문에 비고유)
--    Only when no index is keyed on c_id; non-unique because duplicates may exist.
------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
               FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
               JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID('dbo.charac0') AND c.name = 'c_id')
BEGIN
    CREATE NONCLUSTERED INDEX IX_charac0_c_id ON dbo.charac0 (c_id);
    PRINT 'created IX_charac0_c_id';
END
ELSE PRINT 'charac0 already has an index keyed on c_id';
GO

------------------------------------------------------------------------------
-- 4. 확인 / verify: 세 테이블 모두 c_id 로 Index Seek 가 나와야 한다.
--    '<id>' 를 실제 계정·캐릭터 ID 로 바꿔 실행한다. / replace '<id>' with real IDs.
------------------------------------------------------------------------------
SET STATISTICS IO ON;
SELECT COUNT(*) FROM dbo.account      WHERE c_id = '<id>';
SELECT COUNT(*) FROM dbo.charac0      WHERE c_id = '<id>';
SELECT COUNT(*) FROM dbo.ItemStorage0 WHERE c_id = '<id>';
SET STATISTICS IO OFF;
-- 메시지 탭의 "logical reads" 가 테이블당 2~3 이면 seek, 수십~수백이면 아직 스캔이다.
-- "logical reads" of 2-3 per table = seek; tens or hundreds = still scanning.
GO

------------------------------------------------------------------------------
-- 비고유 대안 / non-unique fallback: 0단계에서 dup_ids > 0 인 테이블에만 사용
------------------------------------------------------------------------------
-- CREATE NONCLUSTERED INDEX IX_account_c_id      ON dbo.account      (c_id);
-- CREATE NONCLUSTERED INDEX IX_ItemStorage0_c_id ON dbo.ItemStorage0 (c_id);

------------------------------------------------------------------------------
-- 되돌리기 / rollback
------------------------------------------------------------------------------
-- DROP INDEX IF EXISTS IX_account_c_id      ON dbo.account;
-- DROP INDEX IF EXISTS IX_ItemStorage0_c_id ON dbo.ItemStorage0;
-- DROP INDEX IF EXISTS IX_charac0_c_id      ON dbo.charac0;
