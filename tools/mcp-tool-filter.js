#!/usr/bin/env node
'use strict';
// stdio MCPサーバーの前段に置く中継。許可リストにないツールを tools/list から除き、
// tools/call も転送せずに拒否する。AI CLIの種類に依存せず、探索で使えるツールを固定するための安全制御。
// 使い方: node mcp-tool-filter.js --allow tool1,tool2 -- <command> [args...]
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const argv = process.argv.slice(2);
const separator = argv.indexOf('--');
const allowIndex = argv.indexOf('--allow');
if (separator < 0 || allowIndex < 0 || allowIndex > separator || separator + 1 >= argv.length) {
  process.stderr.write('usage: mcp-tool-filter.js --allow tool1,tool2 -- <command> [args...]\n');
  process.exit(2);
}
const allowed = new Set((argv[allowIndex + 1] || '').split(',').map((x) => x.trim()).filter(Boolean));
const uploadIndex = argv.indexOf('--upload-root');
const uploadRoot = uploadIndex >= 0 && uploadIndex < separator ? fs.realpathSync(argv[uploadIndex+1]) : null;
function fixtureUploadAllowed(files) {
  if (!Array.isArray(files) || !files.every(file=>typeof file==='string')) return false;
  return files.every(file=>{
    try {
      const resolved=fs.realpathSync(file),relative=path.relative(uploadRoot,resolved);
      return relative && !relative.startsWith('..') && !path.isAbsolute(relative) && fs.statSync(resolved).isFile();
    } catch { return false; }
  });
}
const [command, ...args] = argv.slice(separator + 1);
const child = spawn(command, args, { stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true, cwd: uploadRoot || undefined });
const pendingLists = new Set();

function lines(stream, onLine) {
  let buffer = '';
  stream.setEncoding('utf8');
  stream.on('data', (chunk) => {
    buffer += chunk;
    let index;
    while ((index = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, index);
      buffer = buffer.slice(index + 1);
      if (line.trim().length > 0) onLine(line);
    }
  });
}

function send(stream, message) { stream.write(JSON.stringify(message) + '\n'); }

// クライアント → サーバー
lines(process.stdin, (line) => {
  let message;
  try { message = JSON.parse(line); } catch { child.stdin.write(line + '\n'); return; }
  if (message && message.method === 'tools/list' && message.id !== undefined) pendingLists.add(message.id);
  if (message && message.method === 'tools/call') {
    const name = message.params && message.params.name;
    if (name === 'browser_file_upload' && uploadRoot && !fixtureUploadAllowed(message.params?.arguments?.paths)) {
      if (message.id !== undefined) send(process.stdout,{jsonrpc:'2.0',id:message.id,error:{code:-32602,message:'ScopePilot uploads are limited to generated fixtures for this run.'}});
      process.stderr.write('[mcp-tool-filter] blocked upload outside fixture directory\n');
      return;
    }
    if (!allowed.has(name)) {
      if (message.id !== undefined)
        send(process.stdout, { jsonrpc: '2.0', id: message.id, error: { code: -32601, message: `Tool not allowed by ScopePilot: ${name}` } });
      process.stderr.write(`[mcp-tool-filter] blocked tool call: ${name}\n`);
      return;
    }
  }
  child.stdin.write(line + '\n');
});

// サーバー → クライアント
lines(child.stdout, (line) => {
  let message;
  try { message = JSON.parse(line); } catch { process.stdout.write(line + '\n'); return; }
  if (message && message.id !== undefined && pendingLists.has(message.id)) {
    pendingLists.delete(message.id);
    if (message.result && Array.isArray(message.result.tools))
      message.result.tools = message.result.tools.filter((tool) => allowed.has(tool.name));
    if (uploadRoot && message.result && Array.isArray(message.result.tools))
      for (const tool of message.result.tools)
        if (tool.name === 'browser_file_upload') tool.description += `\nScopePilot: only generated fixture files under ${uploadRoot} may be uploaded. Use the path provided in upload-fixture.json. Other local files are rejected.`;
  }
  send(process.stdout, message);
});

process.stdin.on('end', () => child.stdin.end());
child.on('exit', (code) => process.exit(code === null ? 1 : code));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill());
