// 메모리 모드 서버 대상: 다중 컬럼 레코드 왕복 검증.
// Against the memory-mode server: round-trip the multi-column record.
const net = require('net');
function send(port, line) {
  return new Promise((res) => {
    const c = net.connect(port, '127.0.0.1', () => c.write(line + '\r\n'));
    // 응답은 CP949 → euc-kr 로 디코드(한글 꼬리 비교용). 줄 끝(\n)까지 모은다 — 64KB 넘는 응답은 조각으로 온다.
    // Server replies in CP949; decode as euc-kr. Accumulate until '\n' — replies over 64KB arrive in several chunks.
    let b = '';
    c.on('data', d => { b += new TextDecoder('euc-kr').decode(d); if (b.endsWith('\n')) { c.end(); res(b.replace(/\r?\n$/, '')); } });
    c.on('error', e => res('ERR:' + e.code));
    setTimeout(() => { c.destroy(); res(b || 'TIMEOUT'); }, 1500);
  });
}
let pass = 0, fail = 0;
function chk(name, cond, detail) { console.log((cond ? 'OK  ' : 'FAIL') + ` ${name}` + (detail ? `  ${detail}` : '')); cond ? pass++ : fail++; }
const isDate = s => /^\d{8}\.\d{6}$/.test(s);

(async () => {
  // === charac0 (8880): full 포맷 = 11컬럼 (날짜 2개 + m_body) / full format = 11 columns ===
  const NOID = '해당\\_3ID가\\_3없습니다.', BADFMT = 'MSG\\_3형식\\_3오류';   // 이스케이프된 꼬리 / escaped trailers
  const SAVE = '2071 char01 acct01 0 10 100;0;50 1;1000 5000 A EXP=1\\_1PK=0';
  chk('char-load-missing', (await send(8880, '2011 char01')) === '3011 0 101 해당\\_3Keyword가\\_3없음');
  // 2071 = UPDATE 전용: 없는 레코드는 101, 생성 안 함 / UPDATE only: missing -> 101, no insert
  chk('char-save-missing', (await send(8880, SAVE)) === `3071 0 101 ${NOID}`);
  chk('char-save-not-created', (await send(8880, '2011 char01')).startsWith('3011 0 101'));
  chk('char-save-badfmt', (await send(8880, '2071 char01 acct01 0')) === `3071 0 901 ${BADFMT}`);
  chk('char-create', (await send(8880, '2002 char01 acct01 0 0 0 0 0 A -')) === '3002 1 0 OK');
  chk('char-save', (await send(8880, SAVE)) === '3071 1');
  {
    const r = (await send(8880, '2011 char01')).split(' ');
    // 3011 1 char01 acct01 0 10 <stat> 1;1000 5000 <cdate> <udate> A <m_body>
    const ok = r[0] === '3011' && r[1] === '1' && r[2] === 'char01' && r[3] === 'acct01' &&
      r[4] === '0' && r[5] === '10' && r[6] === '100;0;50' && r[7] === '1;1000' &&
      r[8] === '5000' && isDate(r[9]) && isDate(r[10]) && r[11] === 'A' && r[12] === 'EXP=1\\_1PK=0';
    chk('char-load-after-save', ok, r.join(' '));
  }
  chk('char-create-new', (await send(8880, '2002 char02 acct02 0 0 1;2;3 x y A BODY')) === '3002 1 0 OK');
  // 중복 = 101 Already registered ID. / 토큰 부족 = 901 / dup = 101, short = 901
  chk('char-create-dup', (await send(8880, '2002 char02 acct02 0 0 1;2;3 x y A BODY')) === '3002 0 101 Already\\_3registered\\_3ID.');
  chk('char-create-badfmt', (await send(8880, '2002 char03 acct02 0')) === `3002 0 901 ${BADFMT}`);
  {
    // 생성 직후 로드: d_udate NULL -> 제로날짜 / after create: d_udate is the zero date
    const r = (await send(8880, '2011 char02')).split(' ');
    chk('char-create-udate-null', isDate(r[9]) && r[10] === '18991230.000000', r.join(' '));
  }
  // 2048 = c_status 갱신(soft delete), 행 유지 / 2048 = set c_status, row kept
  chk('char-delete', (await send(8880, '2048 char02 X')) === '3003 1');
  {
    const r = (await send(8880, '2011 char02')).split(' ');
    chk('char-delete-row-kept', r[1] === '1' && r[11] === 'X', r.join(' '));
  }
  chk('char-delete-missing', (await send(8880, '2048 nochar X')) === `3003 0 101 ${NOID}`);
  chk('char-delete-noarg', (await send(8880, '2048 char02')) === `3003 0 901 ${BADFMT}`);

  // === account (7770): account 포맷 = m_body 없음, 날짜 1개(d_udate) / no m_body, one date ===
  chk('acc-missing', (await send(7770, '2021 nouser')).startsWith('3012 0 101'));
  chk('acc-create', (await send(7770, '2002 acct01 - 0 0 0 - - A -')) === '3002 1 0 OK');
  chk('acc-save', (await send(7770, '2071 acct01 nick01 0 0 111111 - user@example.com A -')) === '3071 1');
  {
    const r = (await send(7770, '2021 acct01')).split(' ');
    // 3012 1 acct01 nick01 0 0 111111 - user@example.com <udate> A
    const ok = r[0] === '3012' && r[1] === '1' && r[2] === 'acct01' && r[3] === 'nick01' &&
      r[4] === '0' && r[5] === '0' && r[6] === '111111' && r[7] === '-' && r[8] === 'user@example.com' &&
      isDate(r[9]) && r[10] === 'A' && r.length === 11;  // m_body 없음 / no m_body
    chk('acc-load', ok, r.join(' '));
  }

  // === ItemStorage0 (9990): full 포맷 / full format ===
  chk('stor-create', (await send(9990, '2002 acct01 0 0 0 0 0 0 A -')) === '3002 1 0 OK');
  chk('stor-save', (await send(9990, '2071 acct01 0 0 0 0 0 0 A 100;1;12345;0')) === '3071 1');
  {
    const r = (await send(9990, '2011 acct01')).split(' ');
    const ok = r[0] === '3011' && r[1] === '1' && r[2] === 'acct01' && isDate(r[9]) && isDate(r[10]) &&
      r[11] === 'A' && r[12] === '100;1;12345;0';
    chk('stor-load', ok, r.join(' '));
  }

  // === 프레이밍 (LineReader): 조각 수신 · 한 패킷에 여러 줄 · \n 단독 · \r|\n 분리 · 64KB 초과 줄 · 1MB 초과 차단 ===
  // === framing (LineReader): fragmented bytes, pipelined lines, bare LF, CR|LF split, >64KB line, >1MB rejected ===
  const lines = s => s.split('\n').filter(x => x.length).map(x => x.replace(/\r$/, ''));
  {
    const r = await raw(8880, [...Buffer.from('2011 char01\r\n')].map(b => Buffer.from([b])), 1, 20);
    chk('frame-fragmented', lines(r.data).length === 1 && r.data.startsWith('3011 1 char01 '), r.data.slice(0, 40));
  }
  {
    const r = await raw(8880, [Buffer.from('2011 char01\r\n2011 char02\r\n')], 2);
    const l = lines(r.data);
    chk('frame-pipelined', l.length === 2 && l[0].startsWith('3011 1 char01 ') && l[1].startsWith('3011 1 char02 '), l.map(x => x.slice(0, 20)).join(' | '));
  }
  {
    const r = await raw(8880, [Buffer.from('2011 char01\n')], 1);
    chk('frame-bare-lf', lines(r.data).length === 1 && r.data.startsWith('3011 1 char01 '));
  }
  {
    const r = await raw(8880, [Buffer.from('2011 char01\r'), Buffer.from('\n2011 char02\r\n')], 2, 60);
    const l = lines(r.data);
    chk('frame-crlf-split', l.length === 2 && l[0].startsWith('3011 1 char01 ') && l[1].startsWith('3011 1 char02 '), l.map(x => x.slice(0, 20)).join(' | '));
  }
  {
    const body = 'B'.repeat(70000);   // 수신 버퍼 64KB 초과 → 버퍼 증설 경로 / exceeds the 64KB receive buffer -> grow path
    chk('frame-70k-save', (await send(8880, `2071 char01 acct01 0 10 x y z A ${body}`)) === '3071 1');
    const r = (await send(8880, '2011 char01')).split(' ');
    chk('frame-70k-load', r[1] === '1' && r[12] === body, `len=${r[12]?.length}`);
  }
  {
    const r = await raw(8880, [Buffer.alloc(1100000, 0x41)], 0, 2500);   // 1MB 초과, 종결자 없음 / >1MB, no terminator
    chk('frame-too-long-closes', r.closed && r.data.length === 0, `closed=${r.closed} bytes=${r.data.length}`);
  }

  console.log(`\n== pass ${pass}, fail ${fail} ==`);
  process.exit(fail ? 1 : 0);
})();

// 원시 소켓: chunks 를 간격을 두고 보내고 n 줄(또는 시간 초과)까지 모은다 / raw socket: send chunks with gaps, collect n lines
function raw(port, chunks, n, gapMs = 40) {
  return new Promise((res) => {
    const c = net.connect(port, '127.0.0.1');
    let data = '', closed = false, done = false;
    const finish = () => { if (done) return; done = true; c.destroy(); res({ data, closed }); };
    c.on('connect', async () => { for (const k of chunks) { c.write(k); await new Promise(r => setTimeout(r, gapMs)); } });
    c.on('data', d => { data += d.toString('latin1'); if (n > 0 && data.split('\n').length - 1 >= n) setTimeout(finish, 50); });
    c.on('close', () => { closed = true; if (n === 0) finish(); });
    c.on('error', () => { closed = true; });
    setTimeout(finish, Math.max(1500, chunks.length * gapMs + 1500));
  });
}
