// HARs may exceed V8's maximum string length. Keep file parsing and emission
// bounded by individual JSON strings/chunks, never by the entire document.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { StringDecoder } = require('node:string_decoder');

function readJsonFile(file, progress = () => {}, chunkSize = 64 * 1024) {
  const fd = fs.openSync(file, 'r'), bytes = Buffer.alloc(chunkSize), decoder = new StringDecoder('utf8');
  let chunk = '', position = 0, ended = false, bytesRead = 0;
  function refill() {
    while (position >= chunk.length && !ended) {
      const count = fs.readSync(fd, bytes, 0, bytes.length, null);
      bytesRead += count; position = 0;
      chunk = count ? decoder.write(bytes.subarray(0, count)) : decoder.end();
      ended = !count; progress({ bytesRead });
    }
    return position < chunk.length;
  }
  const peek = () => refill() ? chunk[position] : '';
  const take = () => { const c = peek(); if (c) position++; return c; };
  const space = () => { while (/\s/.test(peek()) && peek()) position++; };
  function expect(expected) { if (take() !== expected) throw new SyntaxError(`Invalid JSON: expected ${expected}`); }
  function string() {
    expect('"'); const parts = ['"']; let skip = false;
    for (;;) {
      if (!refill()) throw new SyntaxError('Unterminated JSON string');
      const start = position;
      if (skip) { position++; skip = false; }
      const special = /["\\]/g;
      special.lastIndex = position;
      let match;
      while ((match = special.exec(chunk))) {
        position = match.index + 1;
        if (match[0] === '"') {
          parts.push(chunk.slice(start, position));
          return JSON.parse(parts.join(''));
        }
        if (position === chunk.length) { skip = true; break; }
        position++; special.lastIndex = position;
      }
      parts.push(chunk.slice(start)); position = chunk.length;
    }
  }
  function value(depth = 0) {
    if (depth > 512) throw new SyntaxError('JSON nesting exceeds 512 levels');
    space(); const c = peek();
    if (c === '"') return string();
    if (c === '{' || c === '[') {
      position++; const object = c === '{', result = object ? {} : [], end = object ? '}' : ']';
      space(); if (peek() === end) { position++; return result; }
      for (;;) {
        let name;
        if (object) { space(); if (peek() !== '"') throw new SyntaxError('Invalid JSON object key'); name = string(); space(); expect(':'); }
        const item = value(depth + 1);
        if (object) Object.defineProperty(result, name, { value: item, writable: true, configurable: true, enumerable: true });
        else result.push(item);
        space(); const separator = take();
        if (separator === end) return result;
        if (separator !== ',') throw new SyntaxError(`Invalid JSON: expected comma or ${end}`);
      }
    }
    let token = '';
    while (peek() && !/[\s,}\]]/.test(peek())) token += take();
    if (!token) throw new SyntaxError('Missing JSON value');
    return JSON.parse(token);
  }
  try {
    const result = value(); space();
    if (peek()) throw new SyntaxError('Unexpected data after JSON value');
    return result;
  } finally { fs.closeSync(fd); }
}

function writeJsonAtomic(file, value, progress = () => {}) {
  const temporary = path.join(path.dirname(file), `.${path.basename(file)}.${process.pid}-${crypto.randomUUID()}.writing`);
  let fd, chunks = [], length = 0, bytesWritten = 0;
  const digest = crypto.createHash('sha256'), ancestors = new Set();
  function flush() {
    if (!length) return;
    const data = Buffer.from(chunks.join(''), 'utf8');
    let offset = 0;
    while (offset < data.length) offset += fs.writeSync(fd, data, offset, data.length - offset);
    digest.update(data); bytesWritten += data.length; chunks = []; length = 0;
    progress({ bytesWritten });
  }
  function append(text) { chunks.push(text); length += text.length; if (length >= 64 * 1024) flush(); }
  function string(text) {
    append('"');
    for (let at = 0; at < text.length; at += 16 * 1024) append(JSON.stringify(text.slice(at, at + 16 * 1024)).slice(1, -1));
    append('"');
  }
  function write(item, inArray = false) {
    if (item && typeof item.toJSON === 'function') item = item.toJSON();
    if (typeof item === 'string') { string(item); return; }
    if (item === null || typeof item !== 'object') {
      const encoded = JSON.stringify(item);
      if (encoded === undefined && !inArray) throw new TypeError('Unsupported JSON value');
      append(encoded === undefined ? 'null' : encoded); return;
    }
    if (ancestors.has(item)) throw new TypeError('Circular JSON value');
    ancestors.add(item);
    if (Array.isArray(item)) {
      append('[');
      for (let index = 0; index < item.length; index++) { if (index) append(','); write(item[index], true); }
      append(']');
    } else {
      append('{'); let first = true;
      for (const name of Object.keys(item)) {
        const child = item[name];
        if (['undefined', 'function', 'symbol'].includes(typeof child)) continue;
        if (!first) append(','); first = false; string(name); append(':'); write(child);
      }
      append('}');
    }
    ancestors.delete(item);
  }
  try {
    fd = fs.openSync(temporary, 'wx'); write(value); flush(); fs.fsyncSync(fd); fs.closeSync(fd); fd = undefined;
    fs.renameSync(temporary, file);
    return { bytes: bytesWritten, sha256: digest.digest('hex') };
  } catch (error) {
    if (fd !== undefined) { try { fs.closeSync(fd); } catch { } }
    try { fs.unlinkSync(temporary); } catch { }
    throw error;
  }
}

function fileIdentity(file) {
  const fd = fs.openSync(file, 'r'), buffer = Buffer.alloc(256 * 1024), digest = crypto.createHash('sha256');
  let bytes = 0, count;
  try { while ((count = fs.readSync(fd, buffer, 0, buffer.length, null))) { digest.update(buffer.subarray(0, count)); bytes += count; } }
  finally { fs.closeSync(fd); }
  return { bytes, sha256: digest.digest('hex') };
}

module.exports = { readJsonFile, writeJsonAtomic, fileIdentity };
