// AI-FluxMux Hail Mary rescue for DeepSeek Harness (dsh 0.2.x, session format v4).
//
// Adds one compaction to a CLOSED Harness chat's session log, using Harness's own installed
// code to build and validate the records, so Harness reopens the same chat with a summary in
// place of the older part. The summary comes from AI-FluxMux (no model call), which is what
// lets this work when the chat is too large for the model to summarise.
//
// Usage: node dsh-rescue.mjs --log <session.v4.jsonl.zstd> --summary-file <utf8 text>
//          [--keep <recent messages kept word for word, default 6>] [--out <path>]
//          [--dsh-root <...\@deepseek-ai\dsh\node_modules\@deepseek-ai>] [--dry-run]
// Prints one JSON line: {"ok":true,...} or {"ok":false,"error":"..."}. Exit code 0 on success.
//
// A chat Harness itself refuses to resume (seen: "tool call id requires a nonempty string", a
// garbled tool call stored at the end of the chat) is repaired first: the broken records at the
// end are dropped (at most 300) until Harness's own stored-log check accepts the chat, and the
// file is rewritten. The caller keeps a backup of the original.
// Harness must not be running on this chat: it holds a write lock on an open chat's log.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { randomUUID } from 'node:crypto';
import { pathToFileURL } from 'node:url';

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}
const flag = (name) => process.argv.includes('--' + name);
function done(obj, code) {
  process.stdout.write(JSON.stringify(obj) + '\n');
  process.exit(code);
}

const ZSTD_MAGIC = 4247762216;
function scanFrames(b) {
  const frames = [];
  let o = 0;
  while (o < b.length) {
    const s = o;
    if (b.length - o < 4 || b.readUInt32LE(o) !== ZSTD_MAGIC) {
      throw new Error('not a Harness session log (bad frame at byte ' + o + ')');
    }
    o += 4;
    const d = b[o++];
    const cs = d >>> 6, ss = (d & 32) !== 0, ck = (d & 4) !== 0, df = d & 3;
    o += (ss ? 0 : 1) + (df === 3 ? 4 : df) + (cs === 0 ? (ss ? 1 : 0) : 1 << cs);
    for (;;) {
      const h = b.readUIntLE(o, 3);
      o += 3;
      const t = h >>> 1 & 3, sz = h >>> 3;
      o += t === 1 ? 1 : sz;
      if (h & 1) break;
    }
    if (ck) o += 4;
    if (o > b.length) throw new Error('session log ends inside a frame (Harness may still be writing it)');
    frames.push([s, o]);
  }
  return frames;
}

function readLog(file) {
  const b = fs.readFileSync(file);
  const lines = [];
  for (const [s, e] of scanFrames(b)) {
    lines.push(...zlib.zstdDecompressSync(b.subarray(s, e)).toString('utf8').split('\n').filter(Boolean));
  }
  const records = lines.map((l) => JSON.parse(l));
  if (records[0]?.type !== 'session') throw new Error('session log has no header');
  const { type: _type, ...header } = records[0];
  const [firstStart, firstEnd] = scanFrames(b)[0];
  return { header, events: records.slice(1), headerFrame: b.subarray(firstStart, firstEnd) };
}

function frameOf(records) {
  const plain = records.map((e) => JSON.stringify(e)).join('\n') + '\n';
  return zlib.zstdCompressSync(Buffer.from(plain, 'utf8'), {
    params: { [zlib.constants.ZSTD_c_checksumFlag]: 1 },
  });
}

function inheritedCount(header, events) {
  if (!header.isSeeded) return undefined;
  const marker = events.find((e) => e.type === 'session/end-seed' && e.data?.inherited === true);
  return marker === undefined ? 0 : marker.seq + 1;
}

const CHECKPOINT_PREAMBLE =
  'This is an automatically generated checkpoint condensing an earlier span of the conversation to free up context. '
  + 'Treat the captured context as established background and build on it without restating it. '
  + 'Continue the task directly from the messages that follow, without acknowledging this checkpoint.';

try {
  const log = arg('log');
  const summaryFile = arg('summary-file');
  if (!log || !summaryFile) done({ ok: false, error: 'need --log and --summary-file' }, 2);
  const keep = Math.max(2, parseInt(arg('keep', '6'), 10) || 6);
  const out = arg('out', log);
  const root = arg('dsh-root', path.join(process.env.APPDATA ?? '', 'npm', 'node_modules', '@deepseek-ai', 'dsh', 'node_modules', '@deepseek-ai'));
  const load = (pkg) => import(pathToFileURL(path.join(root, pkg, 'lib', 'index.js')).href);
  const [sessionLib, llmLib, compactionLib, formatLib] = await Promise.all([
    load('dsh-session'), load('dsh-llm'), load('dsh-compaction'), load('dsh-session-format-v3-to-v4')]);
  const { Session, KNOWN_SESSION_EVENT_TYPES } = sessionLib;
  const { restoreReleasedV4Artifact, assertV4RowAdmission } = formatLib;

  // The check Harness runs when it resumes a stored chat (stricter than Session.create alone).
  const strictError = (hdr, evts) => {
    try {
      for (const row of evts) assertV4RowAdmission(row, KNOWN_SESSION_EVENT_TYPES);
      restoreReleasedV4Artifact({ header: hdr, events: evts, inheritedEventCount: inheritedCount(hdr, evts) ?? 0 }, KNOWN_SESSION_EVENT_TYPES);
      Session.create(hdr.id, evts, hdr, inheritedCount(hdr, evts));
      return null;
    } catch (error) {
      return String(error?.message ?? error);
    }
  };
  const { createUserMessage } = llmLib;
  const { compactCheckpointSource, toolPairingBalancedBefore, toolPairingBalancedAfter } = compactionLib;

  const summaryText = fs.readFileSync(summaryFile, 'utf8').trim();
  if (summaryText.length === 0) done({ ok: false, error: 'summary is empty' }, 2);

  const loaded = readLog(log);
  const header = loaded.header;
  let events = loaded.events;
  let trimmedRecords = 0;
  const resumeError = strictError(header, events);
  if (resumeError !== null) {
    let keepCount = -1;
    for (let k = events.length - 1; k >= Math.max(1, events.length - 300); k--) {
      if (strictError(header, events.slice(0, k)) === null) { keepCount = k; break; }
    }
    if (keepCount < 0) done({ ok: false, error: 'Harness cannot resume this chat and the damage is not at its end: ' + resumeError }, 4);
    trimmedRecords = events.length - keepCount;
    events = events.slice(0, keepCount);
  }
  const originalCount = events.length;
  // Harness's own restore validates every record, sequence and surface transition.
  const session = Session.create(header.id, events, header, inheritedCount(header, events));

  // A frozen chat usually stops inside a turn. Harness closes an interrupted turn itself when
  // it resumes the chat, so leave that to it; tag the compaction with the open turn, as
  // Harness's own overflow compaction does mid-turn.
  let openTurn = null;
  for (let i = events.length - 1; i >= 0; i--) {
    if (events[i].type === 'turn/end') break;
    if (events[i].type === 'turn/start') { openTurn = events[i].data.turn; break; }
  }

  const nodes = [...session.surface.nodes];
  if (nodes.length < keep + 3) done({ ok: false, error: 'chat too short to compact (' + nodes.length + ' messages)' }, 3);
  // Node 0 is the system prompt (protected). Replace node 1 up to just before the kept tail,
  // moving both ends to boundaries that do not split a tool call from its result.
  let startIdx = 1;
  while (startIdx < nodes.length && !toolPairingBalancedBefore(session, nodes[startIdx])) startIdx++;
  let endIdx = nodes.length - 1 - keep;
  while (endIdx > startIdx && !toolPairingBalancedAfter(session, nodes[endIdx])) endIdx--;
  if (endIdx <= startIdx) done({ ok: false, error: 'no safe range to compact' }, 3);
  const shadowedSeqs = nodes.slice(startIdx, endIdx + 1);
  const shadowedTokenCount = shadowedSeqs.reduce(
    (n, seq) => n + Math.ceil(JSON.stringify(session.eventAt(seq).data).length / 4), 0);

  const compactionId = randomUUID();
  const startEvent = session.append('compaction/start', { compactionId, turn: openTurn });
  const summary = [{ type: 'text', text: summaryText }];
  const summaryEvent = session.append('compaction/summary', {
    compactionId,
    summary,
    shadowedRange: { start: nodes[startIdx], end: nodes[endIdx] },
    shadowedSeqs,
    shadowedTokenCount,
    provider: 'ai-fluxmux',
    model: 'hail-mary-session-note',
  });
  const checkpoint = createUserMessage({
    content: [
      { type: 'text', text: CHECKPOINT_PREAMBLE + '\n\n<compacted-summary>' },
      ...summary,
      { type: 'text', text: '</compacted-summary>' },
    ],
    source: compactCheckpointSource(compactionId),
  });
  session.append('user/message', checkpoint, {
    surfaceOp: { op: 'replace', startSeq: nodes[startIdx], endSeq: nodes[endIdx] },
    sourceEventSeqs: [startEvent.seq, summaryEvent.seq, ...shadowedSeqs],
  });
  session.append('compaction/end', { compactionId, turn: openTurn });

  const added = session.log.slice(originalCount).map((e) => JSON.parse(JSON.stringify(e)));
  const frame = frameOf(added);

  // Verify with Harness's own resume check before touching the real log.
  const verifyError = strictError(header, [...events, ...added]);
  if (verifyError !== null) done({ ok: false, error: 'the rebuilt chat would not resume in Harness: ' + verifyError }, 5);
  const verify = Session.create(header.id, [...events, ...added], header, inheritedCount(header, events));
  const result = {
    ok: true,
    sessionId: header.id,
    compactionId,
    openTurn,
    trimmedRecords,
    ...(resumeError === null ? {} : { repaired: resumeError }),
    eventsAdded: added.length,
    replacedMessages: shadowedSeqs.length,
    messagesBefore: nodes.length,
    messagesAfter: verify.surface.nodes.length,
  };
  if (flag('dry-run')) done({ ...result, dryRun: true }, 0);

  if (trimmedRecords > 0) {
    // Rewrite: the original header frame, the kept records, then the compaction.
    const temp = out + '.hailmary-tmp';
    fs.writeFileSync(temp, Buffer.concat([loaded.headerFrame, frameOf(events), frame]));
    fs.renameSync(temp, out);
  } else {
    if (out !== log) fs.copyFileSync(log, out);
    fs.appendFileSync(out, frame);
  }

  const reread = readLog(out);
  const rereadError = strictError(reread.header, reread.events);
  if (rereadError !== null) done({ ok: false, error: 'the written chat failed Harness\'s resume check: ' + rereadError }, 6);
  done({ ...result, written: out }, 0);
} catch (error) {
  done({ ok: false, error: String(error?.message ?? error) }, 1);
}
