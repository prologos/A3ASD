// A3ASD Pay 서버 확인용 클라이언트 / Check client for the A3ASD Pay server
// 사용법 / usage: node payclient.js <host> <port> <id> [ip] [user]
const net = require('net');

const [host = '127.0.0.1', port = '3000', id = '', ip = '127.0.0.1', user = '1'] = process.argv.slice(2);

function buildCheck(id, ip, user) {
  const p = Buffer.alloc(0x2F);
  p.writeUInt32LE(0x2F, 0);             // 패킷 길이 / packet size
  p.writeUInt32LE(Number(user), 4);     // 사용자 번호 / user number
  p[8] = 2; p[9] = 0xE1;                // 종류, 부종류 / type, subtype
  p.write(id.slice(0, 20), 0x0A, 'latin1');
  p.write(ip.slice(0, 15), 0x1F, 'latin1');
  return p;
}

function cstr(buf, off, max) {
  const field = buf.subarray(off, off + max);
  const end = field.indexOf(0);
  return field.subarray(0, end < 0 ? max : end).toString('latin1');
}

function parseReply(r) {
  const days = r.readDoubleLE(0x2F);    // 1899-12-30 기준 일수 / days since 1899-12-30
  return {
    user: r.readUInt32LE(4),
    id: cstr(r, 0x0A, 21),
    ip: cstr(r, 0x1F, 16),
    date: new Date(Date.UTC(1899, 11, 30) + days * 86400000).toISOString().slice(0, 19).replace('T', ' '),
    result: r.subarray(0x38, 0x38 + r[0x37]).toString('latin1'),
  };
}

const sock = net.connect(Number(port), host, () => sock.write(buildCheck(id, ip, user)));
let got = Buffer.alloc(0);
sock.setTimeout(20000, () => { console.error('timeout'); sock.destroy(); process.exitCode = 2; });
sock.on('error', e => { console.error(e.message); process.exitCode = 2; });
sock.on('data', d => {
  got = Buffer.concat([got, d]);
  if (got.length < 4 || got.length < got.readUInt32LE(0)) return;   // 조각 수신 대기 / wait for the rest
  const reply = got.subarray(0, got.readUInt32LE(0));
  console.log(reply.toString('hex').match(/.{1,32}/g).join('\n'));
  const r = parseReply(reply);
  console.log(r, r.result === '000' ? 'EXPIRED' : 'ALLOWED');
  sock.end();
});
