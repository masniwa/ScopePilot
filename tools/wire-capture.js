// Chromium's independent network trace sees Worker bootstrap redirects that the
// Playwright request API/HAR coalesces. Raw traces contain secrets: local only.
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
const crypto = require('node:crypto');
const { readJsonFile, writeJsonAtomic } = require('./har-json');
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const key = r => JSON.stringify([r.method, r.url]);
function headers(lines = []) {
  const result = {};
  for (const line of lines) {
    const at = line.indexOf(':', line.startsWith(':') ? 1 : 0);
    if (at > 0) { const name = line.slice(0, at).toLowerCase(), value = line.slice(at + 1).trim(); result[name] = result[name] ? result[name] + '\n' + value : value; }
  }
  return result;
}
function harHeaders(value) { return Object.entries(value).flatMap(([name, text]) => text.split('\n').map(value => ({ name, value }))); }
function heartbeat(progress) {
  let next = 0;
  return (stage, values = {}, force = false) => { if (force || Date.now() >= next) { next = Date.now() + 10000; progress({ stage, ...values }); } };
}
// A partial NetLog body must not replace a verified, complete body from the
// very same browser event. Never borrow a body merely because its URL matches.
function verifiedBrowserBody(directory, wire, browser, related) {
  const expected = Number(wire.responseHeaders?.['content-length']);
  if (!wire.bodyError || !browser?.body || browser.bodyError || browser.status !== wire.status ||
      !related.some(r => r.kind === 'finished') || related.some(r => r.kind === 'failed') ||
      !Number.isSafeInteger(expected) || expected < 0 || browser.bytes !== expected ||
      (wire.responseHeaders?.['content-encoding'] || 'identity') !== 'identity') return null;
  for (const name of ['content-length', 'content-type', 'content-encoding', 'etag', 'last-modified']) {
    if (browser.headers?.[name] !== undefined && wire.responseHeaders?.[name] !== undefined &&
        browser.headers[name] !== wire.responseHeaders[name]) return null;
  }
  const bodyRoot = path.resolve(directory, 'http-bodies') + path.sep;
  const file = path.resolve(directory, browser.body);
  if (!file.startsWith(bodyRoot) || !fs.existsSync(file)) return null;
  const bytes = fs.readFileSync(file);
  if (bytes.length !== expected || !browser.sha256 || sha(bytes) !== browser.sha256) return null;
  if (wire.body) {
    const partial = fs.readFileSync(path.join(directory, wire.body));
    if (partial.length > bytes.length || !bytes.subarray(0, partial.length).equals(partial)) return null;
  }
  return { body: browser.body, bytes: bytes.length, sha256: browser.sha256 };
}
async function readTrace(directory, allowed, cancelledBeforeSend = [], progress = () => {}) {
  const file = path.join(directory, 'chromium-netlog.json'), sources = new Map(), records = [], jobs = [];
  const stream = fs.createReadStream(file), input = readline.createInterface({ input: stream, crlfDelay: Infinity });
  const report = heartbeat(progress), totalBytes = fs.statSync(file).size;
  report('netlog', { bytesRead: 0, totalBytes }, true);
  let names, offset, complete = false, eventsEnded = false;
  for await (let line of input) {
    report('netlog', { bytesRead: stream.bytesRead, totalBytes, requests: records.length });
    line = line.trim();
    if (line.startsWith('{"constants":')) {
      const constants = JSON.parse(line.replace(/,$/, '') + '}').constants;
      names = Object.fromEntries(Object.entries(constants.logEventTypes).map(([name, id]) => [id, name])); offset = Number(constants.timeTickOffset); continue;
    }
    if (line === ']}') { complete = true; continue; }
    if (line === '}' && eventsEnded) { complete = true; continue; }
    if (line.startsWith('{') && (line.endsWith('],') || line.endsWith(']}'))) {
      complete = line.endsWith(']}'); eventsEnded = true; line = line.slice(0, -2);
    }
    if (!line.startsWith('{')) continue;
    if (!names || !Number.isFinite(offset)) throw Error('NetLog constants/time base missing');
    const e = JSON.parse(line.replace(/,$/, '')), name = names[e.type], p = e.params || {}, at = new Date(offset + Number(e.time)).toISOString();
    if (name === 'URL_REQUEST_START_JOB' && e.phase === 1 && p.url) {
      let scoped = false; try { scoped = allowed.has(new URL(p.url).origin); } catch { }
      const job = { sourceId: e.source.id, url: p.url, method: p.method, scoped, requestedAt: at, ordinal: (sources.get(e.source.id)?.ordinal || 0) };
      sources.set(e.source.id, job); if (scoped) jobs.push(job);
    }
    const source = sources.get(e.source.id); if (!source?.scoped) continue;
    if (name === 'CANCELLED') source.cancelledAt = at;
    if (name === 'REQUEST_ALIVE' && e.phase === 2) source.finishedAt = at;
    if (/^HTTP_TRANSACTION_.*SEND_REQUEST_HEADERS$/.test(name)) {
      const h = headers(p.headers), method = p.line?.split(' ')[0] || h[':method'] || source.method;
      const target = p.line?.split(' ')[1] || h[':path'];
      const url = new URL(target || source.url, source.url); url.hash = '';
      const record = { sourceId: e.source.id, ordinal: ++source.ordinal, method, url: url.href, requestedAt: source.requestedAt, at, headers: h, bodyBytes: 0, bodyChunks: 0 };
      source.current = record; records.push(record);
    }
    const record = source.current; if (!record) continue;
    if (/^HTTP_TRANSACTION_.*READ_RESPONSE_HEADERS$/.test(name)) {
      const h = headers(p.headers), status = Number(/^HTTP\/\S+ (\d+)/.exec(p.headers?.[0] || '')?.[1] || h[':status']);
      if (status >= 200) Object.assign(record, { status, responseHeaders: h, responseAt: at });
    }
    if (name === 'URL_REQUEST_JOB_FILTERED_BYTES_READ' && p.byte_count > 0) {
      if (!p.bytes) { record.bodyError = 'NetLog body bytes unavailable'; continue; }
      const bytes = Buffer.from(p.bytes, 'base64'); if (bytes.length !== p.byte_count) throw Error('NetLog byte count mismatch');
      if (!record.body) { record.body = `http-bodies/netlog-${record.sourceId}-${record.ordinal}.bin`; fs.writeFileSync(path.join(directory, record.body), bytes); }
      else fs.appendFileSync(path.join(directory, record.body), bytes);
      record.bodyBytes += bytes.length; record.bodyChunks++;
    }
    if (p.net_error < 0) record.error = `Chromium net error ${p.net_error}`;
    if (name === 'CANCELLED') record.cancelled = true;
    if (name === 'REQUEST_ALIVE' && e.phase === 2) record.finishedAt = at;
  }
  if (!complete) throw Error('Chromium network log is not finalized');
  report('netlog-bodies', { requests: records.length }, true);
  cancelledBeforeSend.push(...jobs.filter(j => !j.current && j.cancelledAt && j.finishedAt).map(j => ({ sourceId: j.sourceId, method: j.method, url: j.url, requestedAt: j.requestedAt, cancelledAt: j.cancelledAt, finishedAt: j.finishedAt })));
  for (const r of records) {
    report('netlog-bodies', { requests: records.length, sourceId: r.sourceId });
    const empty = r.method === 'HEAD' || [204, 304].includes(r.status) || r.responseHeaders?.['content-length'] === '0';
    if (r.status && !r.body && (empty || (r.finishedAt && !r.error && !r.cancelled && !r.responseHeaders?.['content-length']))) {
      r.body = `http-bodies/netlog-${r.sourceId}-${r.ordinal}.bin`; fs.writeFileSync(path.join(directory, r.body), Buffer.alloc(0));
    }
    if (r.status && !r.body) r.bodyError ||= 'Response body was not exposed by Chromium';
    const expected = Number(r.responseHeaders?.['content-length']), encoding = r.responseHeaders?.['content-encoding'];
    if (r.status && r.body && r.method !== 'HEAD' && ![204, 304].includes(r.status) && r.responseHeaders?.['content-length'] !== undefined && (!encoding || encoding === 'identity') && Number.isFinite(expected) && expected >= 0 && r.bodyBytes !== expected)
      r.bodyError ||= `Response body incomplete: received ${r.bodyBytes} of ${expected} bytes`;
    if (r.body) r.sha256 = sha(fs.readFileSync(path.join(directory, r.body)));
  }
  return records;
}
async function reconcile(directory, allowed, harPath, progress = () => {}) {
  const report = heartbeat(progress);
  const auditPath = path.join(directory, 'capture-audit.json'), summaryPath = path.join(directory, 'wire-capture-summary.json');
  const startedAt = new Date().toISOString();
  writeJsonAtomic(auditPath, { stage: 'wire-reconciling', complete: false, startedAt });
  writeJsonAtomic(summaryPath, { stage: 'reconciling', complete: false, requestCoverageComplete: false, startedAt });
  try {
  const journal = path.join(directory, 'http-transactions.jsonl'), rows = fs.readFileSync(journal, 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse);
  const cancelledBeforeSend = [], wire = await readTrace(directory, allowed, cancelledBeforeSend, progress);
  report('har-reading', { wireRequests: wire.length }, true);
  const har = readJsonFile(harPath, state => report('har-reading', { ...state, wireRequests: wire.length }));
  const details = new Map(rows.filter(r => r.kind === 'request-details').map(r => [r.id, r]));
  const requests = rows.filter(r => r.kind === 'request').map(r => ({ ...r, headers: details.get(r.id)?.headers || r.headers }));
  const browserResponses = new Map(rows.filter(r => r.kind === 'response' && r.status > 0).map(r => [r.id, r]));
  const harQueues = new Map();
  for (const e of har.log.entries) { const k = key(e.request); if (!harQueues.has(k)) harQueues.set(k, []); harQueues.get(k).push(e); }
  const entries = new Map(); for (const r of requests) {
    const e = harQueues.get(key(r))?.shift();
    if (e) {
      e._scopepilotRole = r.role || '認証状態未確認';
      e._scopepilotJournal = { ...e._scopepilotJournal, id: r.id, source: e._scopepilotJournal?.source || 'PlaywrightBrowserEvent' };
      entries.set(r.id, e);
    }
  }
  for (const r of requests) {
    report('matching', { browserRequests: requests.length, wireRequests: wire.length });
    const terminal = rows.filter(x => x.id === r.id && ['response', 'finished', 'failed'].includes(x.kind)).map(x => Date.parse(x.at));
    const start = Date.parse(r.at), entry = entries.get(r.id);
    const harEnd = entry ? Date.parse(entry.startedDateTime) + Math.max(0, Number(entry.time) || 0) : start;
    const end = Math.max(start, Number.isFinite(harEnd) ? harEnd : start, ...terminal);
    // An augmenting match must not move a response-bearing browser occurrence
    // onto an aborted wire occurrence without response headers. Keep status
    // differences allowed: Worker redirects can be coalesced by the browser.
    // A request can wait for a connection longer than this matching tolerance.
    // Correlate its creation time, rather than assigning its late header send to
    // the next browser request for the same URL.
    r.candidates = wire.filter(w => key(w) === key(r) && (!browserResponses.has(r.id) || w.status) && Date.parse(w.requestedAt || w.at) >= start - 5000 && Date.parse(w.requestedAt || w.at) <= end + 5000 && (!r.headers.cookie || r.headers.cookie === (w.headers.cookie || '')))
      .sort((a, b) => Math.abs(Date.parse(a.requestedAt || a.at) - start) - Math.abs(Date.parse(b.requestedAt || b.at) - start));
  }
  const assigned = new Map();
  function assign(r, seen) { for (const w of r.candidates) { if (seen.has(w)) continue; seen.add(w); const prior = assigned.get(w); if (!prior || assign(prior, seen)) { assigned.set(w, r); return true; } } return false; }
  // Preserve the response-bearing occurrence when a nearby request was blocked
  // before transmission. All wire occurrences still receive their own entry.
  for (const r of [...requests].sort((a, b) => Number(rows.some(x => x.id === a.id && x.kind === 'failed')) - Number(rows.some(x => x.id === b.id && x.kind === 'failed')))) assign(r, new Set());
  const browserHar = path.join(directory, 'browser-capture.har');
  if (!fs.existsSync(browserHar)) fs.copyFileSync(harPath, browserHar, fs.constants.COPYFILE_EXCL);
  let sequence = Math.max(0, ...requests.map(r => r.id)), added = 0, requestErrors = 0, recoveredResponseBodies = 0, responseBodyErrors = 0;
  const supplements = [];
  const append = value => { rows.push(value); supplements.push(JSON.stringify(value)); };
  for (const w of wire) {
    report('supplementing', { wireRequests: wire.length, sourceId: w.sourceId });
    const matched = assigned.get(w), id = matched?.id || ++sequence, source = 'ChromiumNetLog';
    let e = matched && entries.get(id);
    if (!matched) {
      added++;
      const bodyMissing = !['GET', 'HEAD'].includes(w.method) && w.headers['content-length'] !== '0';
      if (bodyMissing) requestErrors++;
      append({ kind: 'request', id, at: w.at, method: w.method, url: w.url, headers: w.headers, postDataAvailable: !bodyMissing, postDataBase64: '', role: '認証状態未確認', source, wireSourceId: w.sourceId, wireOrdinal: w.ordinal });
      append({ kind: 'request-details', id, headers: w.headers, bodyMissing, bodyExpectedBytes: Number(w.headers['content-length'] || 0), bodyCapturedBytes: 0, source });
      e = { startedDateTime: w.at, time: -1, request: { method: w.method, url: w.url, httpVersion: 'HTTP/1.1', cookies: [], headers: harHeaders(w.headers), queryString: [...new URL(w.url).searchParams].map(([name, value]) => ({ name, value })), headersSize: -1, bodySize: bodyMissing ? -1 : 0 }, response: { status: -1, statusText: '', httpVersion: '', cookies: [], headers: [], content: { size: 0, mimeType: '' }, redirectURL: '', headersSize: -1, bodySize: -1, _failureText: w.error || 'No response headers' }, cache: {}, timings: { send: -1, wait: -1, receive: -1 }, _scopepilotRole: '認証状態未確認', _scopepilotJournal: { id, source } };
      har.log.entries.push(e);
    }
    const capturedBody = Buffer.from(matched?.postDataBase64 || '', 'base64'), expectedBytes = Number(w.headers['content-length'] || 0);
    if (matched) append({ kind: 'request-details', id, headers: w.headers, bodyMissing: expectedBytes > capturedBody.length, bodyExpectedBytes: expectedBytes, bodyCapturedBytes: capturedBody.length, source });
    append({ kind: 'wire-request', id, source, sourceId: w.sourceId, ordinal: w.ordinal, requestedAt: w.requestedAt, at: w.at, method: w.method, url: w.url, headers: w.headers });
    if (w.status) {
      const response = { kind: 'wire-response', id, source, at: w.responseAt, status: w.status, headers: w.responseHeaders, body: w.body, bytes: w.bodyBytes, sha256: w.sha256, bodyError: w.bodyError };
      const completeBody = w.bodyError && matched && verifiedBrowserBody(directory, w, browserResponses.get(id), rows.filter(r => r.id === id));
      if (completeBody) {
        response.wireBody = { body: w.body, bytes: w.bodyBytes, sha256: w.sha256, bodyError: w.bodyError };
        Object.assign(response, completeBody, { bodySource: 'PlaywrightBrowserEvent' });
        delete response.bodyError;
        recoveredResponseBodies++;
      }
      if (response.bodyError) responseBodyErrors++;
      append(response);
      if (!matched) append({ ...response, kind: 'response' });
      if (e) {
        e._scopepilotBrowserResponseStatus = e.response.status;
        e._scopepilotWire = { source, sourceId: w.sourceId, ordinal: w.ordinal, at: w.at };
        e.request.headers = harHeaders(w.headers);
        const text = response.body && fs.readFileSync(path.join(directory, response.body)).toString('base64');
        e.response = { status: w.status, statusText: '', httpVersion: 'HTTP/1.1', cookies: [], headers: harHeaders(w.responseHeaders), content: { size: response.body ? response.bytes : -1, mimeType: w.responseHeaders['content-type'] || '', ...(response.body ? { text, encoding: 'base64' } : {}), ...(response.bodyError ? { _scopepilotBodyError: response.bodyError } : {}), ...(completeBody ? { _scopepilotBodySource: response.bodySource, _scopepilotWireBody: response.wireBody } : {}) }, redirectURL: w.responseHeaders.location || '', headersSize: -1, bodySize: response.body ? response.bytes : -1 };
      }
    }
    if (w.finishedAt || w.status && !w.error) append({ kind: 'finished', id, at: w.finishedAt || w.responseAt, source });
    else if (w.error) append({ kind: 'failed', id, at: w.responseAt || w.at, error: w.error, source });
  }
  const assignedRequests = new Set(assigned.values()), browserTerminal = new Set(rows.filter(x => ['finished', 'failed'].includes(x.kind)).map(x => x.id));
  let cancelledBeforeTransmission = 0;
  const corresponds = (r, c) => key(r) === key(c) && Math.abs(Date.parse(r.at) - Date.parse(c.requestedAt)) <= 5000 && Date.parse(c.cancelledAt) >= Date.parse(r.at);
  for (const c of cancelledBeforeSend) {
    const candidates = requests.filter(r => !assignedRequests.has(r) && !browserTerminal.has(r.id) && !browserResponses.has(r.id) && corresponds(r, c));
    if (candidates.length !== 1 || cancelledBeforeSend.filter(other => corresponds(candidates[0], other)).length !== 1) continue;
    const r = candidates[0], error = 'Chromium cancelled the request before request headers were sent';
    append({ kind: 'failed', id: r.id, at: c.cancelledAt, error, source: 'ChromiumNetLog', wireSourceId: c.sourceId, cancelledBeforeSend: true });
    browserTerminal.add(r.id); cancelledBeforeTransmission++;
    const entry = entries.get(r.id); if (entry && entry.response.status <= 0) entry.response._failureText = error;
  }
  har.log.entries.sort((a, b) => Date.parse(a.startedDateTime) - Date.parse(b.startedDateTime));
  fs.appendFileSync(journal, supplements.join('\n') + (supplements.length ? '\n' : ''));
  report('har-writing', { requests: sequence }, true);
  har.log._scopepilotReconciliation = { stage: 'wire-complete', requests: sequence, finishedAt: new Date().toISOString() };
  const harIdentity = { fileName: path.basename(harPath), ...writeJsonAtomic(harPath, har, state => report('har-writing', { ...state, requests: sequence })) };
  const responseById = new Map(), detailsById = new Map(), terminal = new Set();
  for (const r of rows) { if (['response', 'wire-response'].includes(r.kind)) responseById.set(r.id, r); if (r.kind === 'request-details') detailsById.set(r.id, r); if (['finished', 'failed'].includes(r.kind)) terminal.add(r.id); }
  const summary = { stage: 'wire', har: harIdentity, journalBytes: fs.statSync(journal).size, requests: sequence, responses: responseById.size, failed: new Set(rows.filter(r => r.kind === 'failed').map(r => r.id)).size, bodyErrors: [...responseById.values()].filter(r => r.bodyError).length, requestErrors: [...detailsById.values()].filter(r => r.bodyMissing).length, unresolvedIds: rows.filter(r => r.kind === 'request' && !terminal.has(r.id)).map(r => r.id), finishedAt: new Date().toISOString() };
  summary.complete = !summary.bodyErrors && !summary.requestErrors && !summary.unresolvedIds.length;
  fs.writeFileSync(path.join(directory, 'capture-summary.json'), JSON.stringify(summary, null, 2));
  const netLogResponseBodyErrors = wire.filter(w => w.bodyError).length;
  const result = { stage: 'reconciled', source: 'ChromiumNetLog', har: harIdentity, journalBytes: summary.journalBytes, wireRequests: wire.length, matchedBrowserRequests: assigned.size, addedRequests: added, cancelledBeforeTransmission, requestCoverageComplete: true, requestBodyErrors: summary.requestErrors, responseBodyErrors, netLogResponseBodyErrors, recoveredResponseBodies, complete: summary.complete && !responseBodyErrors };
  writeJsonAtomic(summaryPath, result);
  report('reconciled', { requests: sequence, wireRequests: wire.length }, true);
  return result;
  } catch (error) {
    const failure = { stage: 'failed', complete: false, requestCoverageComplete: false, errorCode: 'HAR_RECONCILIATION_FAILED', startedAt, failedAt: new Date().toISOString() };
    try { writeJsonAtomic(summaryPath, failure); } catch { }
    try { writeJsonAtomic(auditPath, { ...failure, stage: 'wire-failed' }); } catch { }
    throw error;
  }
}
module.exports = { readTrace, reconcile };
