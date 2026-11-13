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
const policyIndex = argv.indexOf('--policy-engagement');
const policyPath = policyIndex >= 0 && policyIndex < separator ? fs.realpathSync(argv[policyIndex + 1]) : null;
let policy;
try { policy = loadPolicy(); }
catch { process.stderr.write('[mcp-tool-filter] Semantic policy is missing, unreadable or invalid. Rebuild the exploration package. No child server was started.\n'); process.exit(2); }
const policyLog = policyPath ? path.join(path.dirname(policyPath), 'policy-decisions.jsonl') : null;

function loadPolicy() {
  if (!policyPath) return null;
  if (fs.statSync(policyPath).size > 16 * 1024 * 1024) throw Error('ScopePilot policy input exceeds the read limit.');
  const engagement = JSON.parse(fs.readFileSync(policyPath, 'utf8'));
  const original = engagement.forbiddenActions;
  if (!Array.isArray(original) || original.some(x => typeof x !== 'string' || !x.trim())) throw Error('ScopePilot policy source is invalid.');
  if (!original.length) return null;
  const value = engagement.forbiddenActionPolicy;
  if (value?.version !== 1 || value.mode !== 'ai-semantic' || !Array.isArray(value.rules) ||
      value.rules.length !== original.length || value.rules.length > 256 || value.rules.some((rule, index) =>
        !rule || rule.id !== 'rule-' + (index + 1) || rule.text !== original[index] || rule.text.length > 4000))
    throw Error('ScopePilot semantic policy is missing or invalid. Rebuild the exploration package.');
  return value;
}

function policyRequired(name, args) {
  return policy && name?.startsWith('browser_') && !['browser_snapshot', 'browser_network_requests'].includes(name) &&
    !(name === 'browser_tabs' && args?.action === 'list');
}
function textValue(value, max = 2000) { return typeof value === 'string' && value.trim().length > 0 && value.length <= max; }
function plainObject(value) { return value && typeof value === 'object' && !Array.isArray(value); }
function validCheck(check) {
  if (!plainObject(check) || Object.keys(check).some(x => !['operation', 'target', 'effects', 'rules'].includes(x)) ||
      !textValue(check.operation, 500) || !textValue(check.target, 1000) || !textValue(check.effects) ||
      !Array.isArray(check.rules) || check.rules.length !== policy.rules.length) return false;
  const expected = new Set(policy.rules.map(x => x.id));
  for (const rule of check.rules) {
    if (!plainObject(rule) || Object.keys(rule).some(x => !['ruleId', 'interpretation', 'decision', 'reason'].includes(x)) ||
        !expected.delete(rule.ruleId) || !textValue(rule.interpretation) || !textValue(rule.reason) ||
        !['allow', 'block', 'uncertain'].includes(rule.decision)) return false;
  }
  return expected.size === 0;
}

function policySchema() {
  const sentence = maxLength => ({ type: 'string', minLength: 1, maxLength });
  return { type: 'object', additionalProperties: false,
    properties: { operation: sentence(500), target: sentence(1000), effects: sentence(2000),
      rules: { type: 'array', minItems: policy.rules.length, maxItems: policy.rules.length,
        items: { type: 'object', additionalProperties: false,
          properties: { ruleId: { type: 'string', enum: policy.rules.map(x => x.id) },
            interpretation: sentence(2000), decision: { type: 'string', enum: ['allow', 'block', 'uncertain'] }, reason: sentence(2000) },
          required: ['ruleId', 'interpretation', 'decision', 'reason'] } } },
    required: ['operation', 'target', 'effects', 'rules'] };
}

// Only the AI's bounded policy assessment is logged. Never copy tool arguments
// such as typed text, credentials, form values or file paths into this audit.
function auditPolicy(name, args, check, outcome, code) {
  const inputValues = [];
  function values(value, key = '', input = false) {
    if (key === 'scopepilot_policy') return;
    const isInput = input || /text|value|password|username|secret|token|credential|^paths$/i.test(key);
    if (typeof value === 'string' && isInput && value.length > 0) inputValues.push(value);
    else if (value && typeof value === 'object') for (const [name, child] of Object.entries(value)) values(child, name, isInput);
  }
  values(args);
  const distinctInputs = [...new Set(inputValues)].sort((left, right) => right.length - left.length);
  const inputPattern = distinctInputs.length ? new RegExp(distinctInputs.map(value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'g') : null;
  function safe(value) {
    let result = typeof value === 'string' ? value : '';
    // Replace against the original text once. Short values must not recursively
    // match the omission marker inserted for another input value.
    if (inputPattern) result = result.replace(inputPattern, () => '[input value omitted]');
    return result.replace(/https?:\/\/[^\s<>"']+/gi, text => {
      try { const url = new URL(text); url.username = ''; url.password = ''; url.hash = ''; for (const key of [...url.searchParams.keys()]) url.searchParams.set(key, '[omitted]'); return url.href; }
      catch { return '[URL omitted]'; }
    });
  }
  const valid = validCheck(check);
  const record = { observedAt: new Date().toISOString(), tool: name, outcome, code,
    operation: valid ? safe(check.operation) : '', target: valid ? safe(check.target) : '', effects: valid ? safe(check.effects) : '',
    rules: policy.rules.map(rule => {
      const judgement = valid ? check.rules.find(x => x.ruleId === rule.id) : null;
      return { ruleId: rule.id, text: rule.text, interpretation: judgement ? safe(judgement.interpretation) : '',
        decision: judgement?.decision || 'not_assessed', reason: judgement ? safe(judgement.reason) : 'A complete valid assessment was not supplied.' };
    }) };
  fs.appendFileSync(policyLog, JSON.stringify(record) + '\n');
}
function rejectPolicy(message, code) {
  if (message.id !== undefined) send(process.stdout, { jsonrpc: '2.0', id: message.id,
    result: { isError: true, content: [{ type: 'text', text: 'ScopePilot semantic policy rejected this action (' + code + '). Do not execute or retry the prohibited/uncertain operation by another path. Observe the UI with browser_snapshot or browser_network_requests; supply a complete scopepilot_policy assessment only for an operation established to be allowed. No browser action was forwarded.' }] } });
  process.stderr.write('[mcp-tool-filter] blocked semantic policy: ' + code + '\n');
}
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
  // Every action must pass the same inspection; never pass a JSON-RPC batch
  // through as an opaque value that could contain unassessed tools/call items.
  if (Array.isArray(message)) {
    for (const item of message)
      if (item?.id !== undefined) send(process.stdout, { jsonrpc: '2.0', id: item.id,
        error: { code: -32600, message: 'ScopePilot requires individual requests; batch requests are not forwarded.' } });
    process.stderr.write('[mcp-tool-filter] blocked batch request\n');
    return;
  }
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
    const args = message.params?.arguments;
    if (policyRequired(name, args)) {
      const check = args?.scopepilot_policy;
      const code = !validCheck(check) ? 'missing-or-invalid-assessment' : check.rules.some(x => x.decision === 'block') ? 'prohibited-operation' :
        check.rules.some(x => x.decision === 'uncertain') ? 'uncertain-operation' : 'allowed';
      try { auditPolicy(name, args, check, code === 'allowed' ? 'allowed' : 'blocked', code); }
      catch { rejectPolicy(message, 'audit-unavailable'); return; }
      if (code !== 'allowed') { rejectPolicy(message, code); return; }
    }
    // Strip only at the policy-owning inner filter. An outer provider filter
    // without --policy-engagement must preserve this metadata for that owner.
    if (policy && plainObject(args) && Object.hasOwn(args, 'scopepilot_policy')) {
      delete args.scopepilot_policy;
      child.stdin.write(JSON.stringify(message) + '\n');
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
    if (policy && message.result && Array.isArray(message.result.tools))
      for (const tool of message.result.tools) {
        if (!tool.name?.startsWith('browser_') || ['browser_snapshot', 'browser_network_requests'].includes(tool.name)) continue;
        const schema = tool.inputSchema && typeof tool.inputSchema === 'object' ? tool.inputSchema : { type: 'object', properties: {} };
        schema.properties = { ...(schema.properties || {}), scopepilot_policy: policySchema() };
        // The conditional list-only exception is checked on every tools/call.
        if (tool.name !== 'browser_tabs') schema.required = [...new Set([...(schema.required || []), 'scopepilot_policy'])];
        tool.inputSchema = schema;
        tool.description = (tool.description || '') + '\nScopePilot semantic prohibition check: interpret every user rule by meaning, including translations, page purpose, object type, confirmation dialogs, autosave and incidental effects. Before this action provide scopepilot_policy={operation,target,effects,rules:[{ruleId,interpretation,decision,reason}]}. effects is a nonempty description string. Include each rule ID exactly once. Only all-allow assessments are forwarded; block, uncertain or missing assessments are rejected. Do not include typed values, credentials, personal values or file contents in this metadata. Read observations with browser_snapshot/browser_network_requests first; browser_tabs action=list is also exempt. Do not claim an operation is allowed just because labels differ. The original tool arguments remain required and are forwarded after policy metadata is removed. Rules: ' + JSON.stringify(policy.rules);
      }
  }
  send(process.stdout, message);
});

process.stdin.on('end', () => child.stdin.end());
child.on('exit', (code) => process.exit(code === null ? 1 : code));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill());
