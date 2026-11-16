// 메모리 모드 서버 대상: 계정 만료 확인(LoginServer [PAYSERVER] 프로토콜) 검증.
// Against the memory-mode server: account expiry check ([PAYSERVER] protocol).
// 전제: test/H3DB.memory.ini ([pay] port=13000, column=c_sheadera). 7770 으로 계정을 만들어 시험한다.
// Needs test/H3DB.memory.ini ([pay] port=13000, column=c_sheadera). Accounts are created through 7770.
const net = require('net');
const PAY = 13000;
let pass = 0, fail = 0;
function chk(name, cond, detail) { console.log((cond ? 'OK  ' : 'FAIL') + ` ${name}` + (detail ? `  ${detail}` : '')); cond ? pass++ : fail++; }

function line(port, text) {
  return new Promise((res) => {
    const c = net.connect(port, '127.0.0.1', () => c.write(text + '\r\n'));
    c.on('data', d => { c.end(); res(d.toString('latin1').trim()); });
    c.on('error', e => res('ERR:' + e.code));
  });
}
function hello() {
  const b = Buffer.alloc(10);
  b.writeUInt32LE(10, 0); b[8] = 2; b[9] = 0xE0;
  return b;
}
function check(user, id, ip) {
  const b = Buffer.alloc(0x2F);
  b.writeUInt32LE(0x2F, 0); b.writeUInt32LE(user, 4); b[8] = 2; b[9] = 0xE1;
  b.write(id, 0x0A, 'latin1'); b.write(ip, 0x1F, 'latin1');
  return b;
}
function parse(r) {
  const cstr = (o, n) => { const s = r.subarray(o, o + n); const e = s.indexOf(0); return s.subarray(0, e < 0 ? n : e).toString('latin1'); };
  const days = r.readDoubleLE(0x2F);
  return { size: r.readUInt32LE(0), user: r.readUInt32LE(4), type: r[8], sub: r[9], id: cstr(0x0A, 21), ip: cstr(0x1F, 16),
           days, year: new Date(Date.UTC(1899, 11, 30) + days * 86400000).getUTCFullYear(),
           result: r.subarray(0x38, 0x38 + r[0x37]).toString('latin1'), tail: r.readUInt32LE(0x3D), len: r.length };
}
// 연결 하나에서 패킷을 주고 n 개 응답을 모은다 / send on one link, collect n replies
function talk(chunks, n, wait = 700) {
  return new Promise((res) => {
    const c = net.connect(PAY, '127.0.0.1');
    let got = Buffer.alloc(0), closed = false;
    c.on('connect', async () => { for (const k of chunks) { c.write(k); await new Promise(r => setTimeout(r, 40)); } });
    c.on('data', d => { got = Buffer.concat([got, d]); });
    c.on('close', () => { closed = true; });
    c.on('error', () => { closed = true; });
    setTimeout(() => { c.destroy(); const out = []; for (let i = 0; i + 0x41 <= got.length; i += 0x41) out.push(parse(got.subarray(i, i + 0x41))); res({ replies: out, closed, bytes: got.length }); }, wait);
  });
}

(async () => {
  chk('make-limited', (await line(7770, '2002 payfuture 20991231.235959 0 0 - - - A -')) === '3002 1 0 OK');
  chk('make-expired', (await line(7770, '2002 paypast 20000101.000000 0 0 - - - A -')) === '3002 1 0 OK');
  chk('make-nolimit', (await line(7770, '2002 paynull - 0 0 - - - A -')) === '3002 1 0 OK');
  chk('make-ancient', (await line(7770, '2002 payancient 00500101.000000 0 0 - - - A -')) === '3002 1 0 OK');

  let r = await talk([hello()], 0, 300);
  chk('hello-no-reply', r.bytes === 0 && !r.closed, `bytes=${r.bytes}`);

  r = await talk([check(971503, 'payfuture', '192.0.2.10')], 1);
  let p = r.replies[0];
  chk('future-allowed', p && p.result === '100' && p.year === 2099, JSON.stringify(p));
  chk('reply-header', p && p.size === 0x41 && p.user === 971503 && p.type === 2 && p.sub === 0xE0 && p.tail === 0);
  chk('reply-echo', p && p.id === 'payfuture' && p.ip === '192.0.2.10');

  r = await talk([check(2, 'paypast', '1.2.3.4')], 1);
  p = r.replies[0];
  chk('past-rejected', p && p.result === '000' && p.year === 2000, JSON.stringify(p));

  // ToOADate 하한 미만도 응답한다 / values below the ToOADate limit still get a reply
  r = await talk([check(9, 'payancient', '1.2.3.4')], 1);
  p = r.replies[0];
  chk('ancient-rejected', p && p.result === '000' && p.year === 100 && !r.closed, JSON.stringify(p));

  r = await talk([check(3, 'paynull', '1.2.3.4')], 1);
  p = r.replies[0];
  chk('null-allowed', p && p.result === '100' && p.year >= 2026, JSON.stringify(p));

  r = await talk([check(4, 'nobody', '1.2.3.4')], 1);
  chk('no-row-allowed', r.replies[0]?.result === '100');

  // 조각 수신 + 한 번에 여러 패킷 / fragmented read + several packets at once
  const c1 = check(5, 'paypast', '1.1.1.1');
  r = await talk([c1.subarray(0, 3), c1.subarray(3, 20), c1.subarray(20)], 1);
  chk('fragmented', r.replies[0]?.result === '000' && r.replies[0]?.user === 5);
  r = await talk([Buffer.concat([hello(), check(6, 'payfuture', '1.1.1.1'), check(7, 'paypast', '1.1.1.1')])], 2);
  chk('pipelined', r.replies.length === 2 && r.replies[0].user === 6 && r.replies[0].result === '100' && r.replies[1].user === 7 && r.replies[1].result === '000');

  // 잘못된 길이는 연결을 끊는다 / a bad size closes the link
  const bad = Buffer.alloc(10); bad.writeUInt32LE(0x10000, 0);
  r = await talk([bad], 0, 400);
  chk('bad-size-closes', r.closed && r.bytes === 0);
  const unk = hello(); unk[8] = 9;
  r = await talk([unk, check(8, 'paynull', '1.1.1.1')], 1);
  chk('unknown-then-ok', r.replies[0]?.user === 8);

  console.log(`\n== pass ${pass}, fail ${fail} ==`);
  process.exit(fail ? 1 : 0);
})();
