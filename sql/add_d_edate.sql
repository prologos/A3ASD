-- A3ASD Pay: 계정 만료일 컬럼 추가 / add the account expiry column
--
-- Pay 서버는 account.d_edate 로 로그인 허용 여부를 판정한다.
--   d_edate 가 NULL 이거나 컬럼이 없으면 만료 확인을 하지 않는다(항상 허용).
--   현재 시각(getdate()) >= d_edate 이면 로그인 거부.
-- The Pay server decides login by account.d_edate.
--   NULL or missing column = no limit (always allowed); getdate() >= d_edate = rejected.
--
-- 주의 / Notes:
--   * 컬럼 추가 후 A3ASD 재시작 없이 바로 적용된다. / Takes effect immediately, no restart.
--   * 열 목록 없는 INSERT (INSERT INTO account VALUES (...)) 를 쓰는 도구가 있으면 컬럼 수가 달라져 실패한다.
--     Tools that INSERT without a column list will break because the column count changes.
--   * 시험 DB 에서 먼저 실행한다. / Run on a test DB first.

USE A3;
GO

------------------------------------------------------------------------------
-- 1. 컬럼 추가 (여러 번 실행해도 안전) / add the column (safe to re-run)
------------------------------------------------------------------------------
IF COL_LENGTH('dbo.account', 'd_edate') IS NULL
BEGIN
    ALTER TABLE dbo.account ADD d_edate datetime NULL;
    PRINT 'added account.d_edate';
END
ELSE PRINT 'account.d_edate already exists';
GO

------------------------------------------------------------------------------
-- 2. 관리 예시 (필요한 것만 주석을 풀어 실행) / management examples (uncomment as needed)
------------------------------------------------------------------------------
-- 만료일 지정 / set expiry
-- UPDATE dbo.account SET d_edate = '2026-12-31 23:59:59' WHERE c_id = '<account>';

-- 30일 연장: 남은 기간이 있으면 그 뒤로, 이미 만료됐으면 지금부터
-- Extend 30 days: from the current expiry if still valid, otherwise from now
-- UPDATE dbo.account
-- SET d_edate = DATEADD(day, 30, CASE WHEN d_edate > GETDATE() THEN d_edate ELSE GETDATE() END)
-- WHERE c_id = '<account>';

-- 제한 해제 / remove the limit
-- UPDATE dbo.account SET d_edate = NULL WHERE c_id = '<account>';

-- 만료된 계정 조회 / list expired accounts
-- SELECT c_id, d_edate FROM dbo.account WHERE d_edate <= GETDATE() ORDER BY d_edate;

------------------------------------------------------------------------------
-- 되돌리기 / rollback
------------------------------------------------------------------------------
-- ALTER TABLE dbo.account DROP COLUMN d_edate;
