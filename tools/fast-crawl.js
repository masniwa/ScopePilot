const fs = require('fs');
const path = require('path');
const net = require('net');
const readline = require('readline');
const crypto = require('crypto');
const { chromium } = require('./playwright-runtime/node_modules/playwright');
const policy = require('./crawl-policy');
const capture = require('./capture-ledger');
const wireCapture = require('./wire-capture');
const runDirectory = process.argv[2];
if (!runDirectory) throw Error('run directory is required');
const interactive = process.argv.includes('--interactive');
const headless = !interactive || process.argv.includes('--headless');
const engagement = JSON.parse(fs.readFileSync(path.join(runDirectory, 'engagement.json'), 'utf8'));
const proxyRuntime = JSON.parse(fs.readFileSync(path.join(runDirectory, 'exploration-proxy.json'), 'utf8'));
const proxy = new URL(proxyRuntime.url);
if (proxy.protocol !== 'http:' || !(proxy.hostname.startsWith('127.') || ['localhost', '[::1]'].includes(proxy.hostname)) || proxy.username || proxy.password || proxy.pathname !== '/' || proxy.search || proxy.hash) throw Error('local HTTP exploration proxy is required');
const allowed = new Set(engagement.allowedOrigins.map(x => new URL(x).origin));
const supporting = new Set((engagement.supportingOrigins || []).map(x => new URL(x).origin));
const startUrl = policy.normalizeUrl(engagement.StartUrl);
// Fragments select SPA screens, while HTTP request identities omit fragments.
const initialNavigationUrl = new URL(engagement.StartUrl).href;
if (!allowed.has(new URL(startUrl).origin)) throw Error('Start URL origin/port is outside allowed origins');
if (engagement.afterAuthenticationUrl && !allowed.has(new URL(engagement.afterAuthenticationUrl).origin)) throw Error('Authentication target is outside allowed origins');
const startedAt = new Date();
const deadline = Date.now() + Math.max(1, Math.floor(engagement.limits.MaxMinutes * 0.7)) * 60000;
const maxPages = engagement.limits.MaxPages, maxRequests = engagement.limits.MaxRequests;
if (!(maxPages > 0 && maxRequests > 0 && engagement.limits.MaxMinutes > 0)) throw Error('Invalid crawl limits');
const queue = [], queued = new Set(), visited = new Set(), pages = [], coverage = new Map(), requests = new Map(), requestRoles = new WeakMap(), skipped = [];
let currentRole = '未認証', manualAuthentication = false, authenticationBlocked = false, errorCount = 0, requestCount = 0, failureCode = null;
// Form submission is opt-in (engagement.crawlFormSubmissions): each selected form is submitted
// once with harmless placeholder values so its exchange lands in the HAR for review.
const formSubmissionLimit = engagement.crawlFormSubmissions ? Math.max(1, Math.min(25, maxPages)) : 0;
const submittedForms = new Set();
const formSubmissions = { enabled: !!engagement.crawlFormSubmissions, attempted: 0, succeeded: 0, skipped: [] };
let formSubmissionWindow = false;
let crawlComplete = false;
const aiInteractions = engagement.aiInteractions === true;
const runDeadline = Date.now() + engagement.limits.MaxMinutes * 60000;
const aiPages = new Set();
function blockedRequest(request, reason) {
  fs.appendFileSync(path.join(runDirectory, 'blocked-requests.jsonl'), JSON.stringify({ method: request.method(), url: displayUrl(request.url()), reason, observedAt: new Date().toISOString() }) + '\n');
}
let context, browser, page, ledger, closing = false, closeRequested = false, closeResolve;
const closeSignal = new Promise(resolve => { closeResolve = resolve; });
const input = readline.createInterface({ input: process.stdin });
input.on('line', line => { if (line.trim() === 'close') { closeRequested = true; closeResolve(); } });
input.on('close', () => { if (interactive) { closeRequested = true; closeResolve(); } });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
function emit(type, value) { process.stdout.write(type + ' ' + JSON.stringify(value) + '\n'); }
function atomic(name, value) { const dest = path.join(runDirectory, name), tmp = dest + '.tmp'; fs.writeFileSync(tmp, JSON.stringify(value, null, 2), 'utf8'); fs.renameSync(tmp, dest); }
function displayUrl(value) { const u = new URL(value); u.search = ''; u.hash = /^#!?\//.test(u.hash) ? u.hash.split('?')[0] : ''; u.username = ''; u.password = ''; return u.href; }
function key(url, role = currentRole) { return role + ' ' + url; }
function enqueue(value, from = '', label = '') {
  let url; try { url = policy.normalizeUrl(value); } catch { return; }
  const k = key(url);
  if (coverage.has(k)) return;
  const blocked = !allowed.has(new URL(url).origin) ? 'outside-origin' : policy.externalNavigationTarget(url, allowed) || policy.reason(url, label, engagement.forbiddenActions || []);
  coverage.set(k, { url, role: currentRole, discoveredFrom: from, label: label.slice(0, 160), status: blocked ? 'skipped' : 'queued', reason: blocked || '' });
  if (blocked) { skipped.push({ url, reason: blocked }); return; }
  queued.add(k); queue.push({ url, role: currentRole });
}
function persist() {
  const lines = [...requests.values()].map(x => JSON.stringify(x)).join('\n') + (requests.size ? '\n' : '');
  fs.writeFileSync(path.join(runDirectory, 'crawl-pages.jsonl'), pages.map(x => JSON.stringify(x)).join('\n') + (pages.length ? '\n' : ''), 'utf8');
  if (!crawlComplete) fs.writeFileSync(path.join(runDirectory, 'fast-observed-requests.jsonl'), lines, 'utf8');
  fs.writeFileSync(path.join(runDirectory, 'observed-requests.jsonl'), lines, 'utf8');
  atomic('crawl-coverage.json', { generatedAt: new Date(), pages: [...coverage.values()] });
}
async function inspect() { return await page.evaluate(policy.inspectDocument); }
async function inspectReady() {
  // SPA menus/forms often appear only after the initial API requests finish.
  // Bound the wait for polling/streaming pages instead of assuming 150 ms is ready.
  await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { });
  return inspect();
}
function recordNavigation(state, status) {
  const value = { status, actualUrl: displayUrl(state.url), loginFormVisible: policy.isLogin(state), authenticatedIndicatorVisible: state.authenticatedIndicator, activeRole: currentRole, checkedAt: new Date() };
  atomic('authentication-navigation.json', value); emit('NAVIGATION', value);
}
async function authenticate(initial) {
  if (!interactive) { authenticationBlocked = true; return null; }
  manualAuthentication = true;
  try {
    while (Date.now() < deadline && !closeRequested) {
      const request = { requestId: crypto.randomUUID(), kind: 'authentication', reason: '認証画面が表示されています。', page: displayUrl(page.url()), instruction: '現在の探索ブラウザへユーザー名・パスワードを直接入力してログインし、ScopePilotで認証後のロールを選んで再開してください。' };
      atomic('human-intervention.json', request); emit('INTERVENTION', request);
      recordNavigation(await inspect(), 'waiting');
      let signal;
      while (Date.now() < deadline && !closeRequested) {
        try { const s = JSON.parse(fs.readFileSync(path.join(runDirectory, 'resume.signal'), 'utf8')); if (s.requestId === request.requestId && s.activeRole && !/未認証|^anonymous$|^guest$/i.test(s.activeRole)) { signal = s; break; } } catch { }
        await sleep(200);
      }
      if (!signal) { authenticationBlocked = true; return null; }
      let state;
      for (let i = 0; i < 20; i++) {
        if (page.isClosed()) { authenticationBlocked = true; return null; }
        for (const candidate of context.pages()) {
          if (candidate !== page && allowed.has(new URL(candidate.url()).origin)) {
            const old = page; page = candidate;
            const check = await inspect().catch(() => null);
            if (check && !policy.isLogin(check) && check.authenticatedIndicator) break;
            page = old;
          }
        }
        state = await inspect().catch(() => null);
        if (state && allowed.has(new URL(state.url).origin) && !policy.isLogin(state) && state.authenticatedIndicator) break;
        await sleep(500);
      }
      fs.rmSync(path.join(runDirectory, 'resume.signal'), { force: true });
      if (!state || !allowed.has(new URL(state.url).origin) || policy.isLogin(state) || !state.authenticatedIndicator) { recordNavigation(state || initial, 'failed'); continue; }
      state = await inspectReady();
      currentRole = signal.activeRole;
      for (const [k, r] of [...requests]) if (r.method === 'GET' && r.url === policy.normalizeUrl(state.url)) {
        requests.delete(k); r.role = currentRole; requests.set(`${r.role} ${r.method} ${r.url}`, r);
      }
      if (engagement.afterAuthenticationUrl) {
        await page.goto(engagement.afterAuthenticationUrl, { waitUntil: 'domcontentloaded', timeout: 20000 });
        state = await inspectReady();
        if (policy.isLogin(state) || !state.authenticatedIndicator || !allowed.has(new URL(state.url).origin)) { recordNavigation(state, 'failed'); continue; }
      }
      recordNavigation(state, 'authenticated');
      atomic('engagement.json', { ...engagement, activeRole: currentRole });
      fs.rmSync(path.join(runDirectory, 'human-intervention.json'), { force: true });
      return state;
    }
    authenticationBlocked = true; return null;
  } finally { manualAuthentication = false; }
}
async function submitForms(forms, pageUrl) {
  for (const form of forms) {
    if (submittedForms.size >= formSubmissionLimit) break;
    let actionKey;
    try { actionKey = form.method + ' ' + policy.normalizeUrl(form.action); } catch { continue; }
    const forbidden = engagement.forbiddenActions || [];
    const blocked = policy.reason(form.action, [form.formLabel,form.submitLabel].filter(Boolean).join(' '), forbidden);
    if (blocked) { formSubmissions.skipped.push({ url: form.action, reason: blocked }); continue; }
    if (form.hasPasswordField || form.hasFileField) { formSubmissions.skipped.push({ url: form.action, reason: 'credential-or-upload-form' }); continue; }
    if (submittedForms.has(actionKey)) continue;
    submittedForms.add(actionKey);
    formSubmissions.attempted++;
    try {
      formSubmissionWindow = true;
      // Subscribe before submitting. A fixed delay can race navigation and lose the POST response.
      const responsePending = page.waitForResponse(response => response.request().method() === form.method &&
        policy.normalizeUrl(response.url()) === policy.normalizeUrl(form.action), { timeout: 15000 }).catch(() => null);
      const result = await page.evaluate(fixture => {
        function setValue(element, value) {
          const proto = element.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
          if (descriptor && descriptor.set) descriptor.set.call(element, value); else element.value = value;
          element.dispatchEvent(new Event('input', { bubbles: true }));
        }
        const sameForm=f=>(f.getAttribute('method')||'GET').toUpperCase()===(fixture.formMethod||fixture.method) && new URL(f.getAttribute('action')||location.href,location.href).href===(fixture.formAction||fixture.action);
        const form = Number.isInteger(fixture.formIndex)?document.forms[fixture.formIndex]:[...document.forms].find(sameForm);
        if (!form) return 'form-not-found';
        if (!sameForm(form)) return 'form-changed';
        if (form.querySelector('input[type=file]')) return 'credential-or-upload-form';
        const controls=[...Object.getOwnPropertyDescriptor(HTMLFormElement.prototype,'elements').get.call(form)];
        const words=value=>String(value||'').replace(/([a-z\d])([A-Z])/g,'$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g,'$1 $2').replace(/[_\-]+/g,' ');
        const contextualUrl=element=>{
          if(!element.name||element.disabled||!['INPUT','TEXTAREA'].includes(element.tagName))return false;
          const type=(element.type||'').toLowerCase();
          if(element.tagName==='INPUT'&&!['text','search','tel','email','url',''].includes(type))return false;
          if(type==='url')return true;
          const identity=[element.name,element.id,element.autocomplete].filter(Boolean).join(' ');
          if(/\b(?:url|uri|href|website|homepage|link)\b/i.test(words(identity)) || /\b(?:image|photo|avatar|profile|source|target|redirect|callback|return|resource|endpoint|base|feed|document|video|audio|icon|logo)(?:url|uri)\b/i.test(identity))return true;
          const hints=[element.getAttribute('aria-label'),element.getAttribute('title'),element.getAttribute('placeholder'),
            ...[...(element.labels||[])].map(label=>label.textContent),
            ...(element.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>element.getRootNode().getElementById?.(id)?.textContent)];
          return hints.some(value=>/\b(?:url|uri|href)\b|web\s+address|リンク先|ウェブサイト|ホームページ/i.test(words(value)) || /^(?:website|home\s?page|link)\s*[:*]?\s*$/i.test(String(value||'').trim()) || /(?:https?|ftp):\/\//i.test(String(value||'')));
        };
        // A URL can trigger server-side retrieval or overwrite an existing resource.
        // Defer before changing ANY field, including valid/prefilled URLs; the AI
        // must establish the real value and its effects from the visible workflow.
        if(controls.some(contextualUrl))return 'contextual-url-input';
        for (const element of controls) {
          if (!element.name || element.disabled || element.readOnly) continue;
          const type = (element.type || '').toLowerCase();
          if (['text', 'search', 'tel', ''].includes(type)) setValue(element, 'ScopePilot確認用入力');
          else if (type === 'email') setValue(element, 'scopepilot@example.invalid');
          else if (type === 'number') setValue(element, '1');
          else if (element.tagName === 'TEXTAREA') setValue(element, 'ScopePilot確認用入力');
        }
        const submitter = controls.find(e=>e.matches('button:not([type]),button[type=submit],input[type=submit]'));
        if (!HTMLFormElement.prototype.requestSubmit) return 'unsupported-form-submit';
        let observed=false;
        const check=()=>{observed=true;};
        window.addEventListener('submit',check,true);
        try { HTMLFormElement.prototype.requestSubmit.call(form,submitter||undefined); }
        finally { window.removeEventListener('submit',check,true); }
        return observed?'submitted':'form-submit-prevented';
      }, { method: form.method, action: form.action, formIndex:form.formIndex, formMethod:form.formMethod, formAction:form.formAction });
      if (result !== 'submitted') { formSubmissions.skipped.push({ url: form.action, reason: result }); continue; }
      const submittedResponse = await responsePending;
      if (!submittedResponse) {
        errorCount++;
        formSubmissions.skipped.push({ url: form.action, reason: 'response-unconfirmed' });
        continue;
      }
      await page.waitForLoadState('domcontentloaded', { timeout: 15000 }).catch(() => { });
      await page.waitForTimeout(250);
      formSubmissions.succeeded++;
      emit('FORM_SUBMITTED', { url: form.action, method: form.method, role: currentRole });
      await page.goto(pageUrl, { waitUntil: 'domcontentloaded', timeout: 20000 }).catch(() => { });
      await page.waitForTimeout(150);
    } catch (error) {
      formSubmissions.skipped.push({ url: form.action, reason: String(error.message || error).split('\n')[0].slice(0, 120) });
    } finally { formSubmissionWindow = false; }
  }
}
async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port;
}
async function discoverSitemaps() {
  // Keep discovery HTTP on the same recorded/proxied path as normal navigation.
  // APIRequestContext traffic does not emit BrowserContext request events/HAR.
  const discoveryPage = await context.newPage();
  try {
  for (const origin of allowed) {
    if (Date.now() >= deadline) break;
    const candidates = new Set([`${origin}/sitemap.xml`]);
    try {
      const r = await discoveryPage.goto(`${origin}/robots.txt`, { timeout: 5000, waitUntil: 'domcontentloaded' });
      if (r?.ok()) for (const line of (await r.text()).split(/\r?\n/)) { const m = /^\s*sitemap\s*:\s*(\S+)/i.exec(line); if (m) candidates.add(m[1]); }
    } catch { }
    for (const candidate of candidates) {
      if (!allowed.has(new URL(candidate).origin) || Date.now() >= deadline) continue;
      try {
        const r = await discoveryPage.goto(candidate, { timeout: 5000, waitUntil: 'domcontentloaded' });
        if (!r?.ok()) continue;
        const body = await r.text();
        if (body.length > 2000000) continue;
        for (const m of body.matchAll(/<loc>\s*([^<]+?)\s*<\/loc>/gi)) enqueue(m[1].trim().replace(/&amp;/g, '&'), candidate, 'sitemap');
      } catch { }
    }
  }
  } finally {
    await discoveryPage.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { });
    await discoveryPage.close();
  }
}
async function main() {
  try {
    const networkArgs = [`--log-net-log=${path.join(runDirectory, 'chromium-netlog.json')}`, '--net-log-capture-mode=Everything'];
    const options = { channel: 'msedge', headless, serviceWorkers: aiInteractions ? 'block' : 'allow', ignoreHTTPSErrors: true, proxy: { server: proxyRuntime.url }, recordHar: { path: path.join(runDirectory, 'fast-capture.har.pending'), mode: 'full', content: 'embed' } };
    if (interactive) {
      const port = await freePort();
      context = await chromium.launchPersistentContext(path.join(runDirectory, 'browser-profile'), { ...options, args: [...networkArgs, `--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1'] });
      atomic('browser-runtime.json', { cdpEndpoint: `http://127.0.0.1:${port}`, status: 'ready' });
    } else {
      browser = await chromium.launch({ channel: 'msedge', headless: true, proxy: { server: proxyRuntime.url }, args: networkArgs });
      context = await browser.newContext({ serviceWorkers: options.serviceWorkers, ignoreHTTPSErrors: true, recordHar: options.recordHar });
    }
    if (aiInteractions) await context.routeWebSocket('**/*', socket => socket.close());
    {
      const config = { forbidden: engagement.forbiddenActions || [] };
      const observe = p => p.on('console', message => {
        if (message.text().startsWith('[scopepilot-interaction-blocked]'))
          fs.appendFileSync(path.join(runDirectory, 'blocked-actions.jsonl'), JSON.stringify({ reason: message.text(), observedAt: new Date().toISOString() }) + '\n');
      });
      context.on('page', observe);
      for (const p of context.pages()) observe(p);
      // Install before the first navigation. PDF downloads can leave a transient
      // about:blank target whose execution context never becomes ready for evaluate.
      await context.addInitScript(policy.installInteractionGuard, config);
    }
    ledger = capture.attach(context, runDirectory, allowed, () => currentRole);
    await context.route('**/*', async route => {
      const r = route.request(); let u; try { u = new URL(r.url()); } catch { return route.abort('blockedbyclient'); }
      if (closing) { blockedRequest(r, 'capture-closing'); return route.abort('blockedbyclient'); }
      if (!allowed.has(u.origin) && !(manualAuthentication && supporting.has(u.origin))) return route.abort('blockedbyclient');
      // Explicit prohibitions have no crawl, form-submit or authentication bypass.
      const forbidden = policy.mutationReason(r.url(), r.method(), engagement.forbiddenActions || []);
      if (forbidden) { blockedRequest(r, forbidden); return route.abort('blockedbyclient'); }
      if (r.isNavigationRequest() && policy.externalNavigationTarget(r.url(), manualAuthentication ? new Set([...allowed,...supporting]) : allowed)) {
        blockedRequest(r, 'outside-origin-redirect-link'); return route.abort('blockedbyclient');
      }
      if (requestCount >= maxRequests) return route.abort('blockedbyclient');
      const aiPhase = crawlComplete && aiInteractions;
      if (aiPhase && engagement.deferLogoutUntilFinalization === true && policy.isLogout(r.url())) {
        let phase;
        try { phase = JSON.parse(fs.readFileSync(path.join(runDirectory, 'exploration-phase.json'), 'utf8')); } catch { }
        if (!(phase?.stage === 'finalization' && phase.allowLogout === true)) {
          blockedRequest(r, 'logout-deferred-until-finalization');
          return route.abort('blockedbyclient');
        }
      }
      if (aiPhase && Date.now() >= runDeadline) { blockedRequest(r, 'time-limit'); return route.abort('blockedbyclient'); }
      if (!aiPhase && !manualAuthentication && !formSubmissionWindow && !['GET', 'HEAD'].includes(r.method())) {
        blockedRequest(r, 'read-only-mode'); return route.abort('blockedbyclient');
      }
      if (!aiPhase && !manualAuthentication && r.isNavigationRequest() && policy.reason(r.url()) === 'state-changing-link') return route.abort('blockedbyclient');
      if (crawlComplete && policy.isTopLevelNavigation(r) && !visited.has(key(policy.normalizeUrl(r.url())))) {
        if (!aiPhase) return route.abort('blockedbyclient');
        const url = policy.normalizeUrl(r.url());
        if (!aiPages.has(url) && pages.length + aiPages.size >= maxPages) { blockedRequest(r, 'page-limit'); return route.abort('blockedbyclient'); }
        aiPages.add(url);
      }
      return route.continue();
    });
    context.on('request', r => {
      requestCount++;
      if (crawlComplete && aiInteractions && /\/(?:login|logout|log-in|log-out|signin|signout|sign-in|sign-out)(?:\/|\?|$)/i.test(r.url()))
      {
        currentRole = '認証状態未確認';
        fs.appendFileSync(path.join(runDirectory, 'authentication-changes.jsonl'), JSON.stringify({ observedAt: new Date().toISOString(), role: currentRole }) + '\n');
      }
      requestRoles.set(r, currentRole);
    });
    context.on('response', response => {
      const r = response.request(); let url; try { url = policy.normalizeUrl(response.url()); } catch { return; }
      if (!allowed.has(new URL(url).origin)) return;
      const role = requestRoles.get(r) || currentRole, k = `${role} ${r.method()} ${url}`;
      if (!requests.has(k)) requests.set(k, { method: r.method(), url, statusCode: response.status(), contentType: response.headers()['content-type'] || '', source: 'PlaywrightFastCrawler', role, pageTitle: '', observedAt: new Date().toISOString() });
    });
    page = context.pages()[0] || await context.newPage();
    enqueue(startUrl); await discoverSitemaps();
    // Document-derived crawl targets: the app already filtered them to allowed
    // origins; enqueue() re-applies origin/policy/limit checks like any link.
    for (const target of (engagement.documentHints && engagement.documentHints.crawlTargets) || [])
      enqueue(target, 'document-hint', '文書記載エンドポイント');
    while (queue.length && pages.length < maxPages && requestCount < maxRequests && Date.now() < deadline && !closeRequested) {
      const item = queue.shift(), k = key(item.url, item.role);
      if (visited.has(k)) continue;
      const row = coverage.get(k);
      try {
        const response = await page.goto(item.url === startUrl && !pages.length ? initialNavigationUrl : item.url, { waitUntil: 'domcontentloaded', timeout: 20000 });
        let state = await inspectReady();
        if (!allowed.has(new URL(state.url).origin)) throw Error('redirected outside allowed origin');
        if (policy.isLogin(state)) {
          if (!pages.some(p => p.url === state.url && p.role === currentRole)) pages.push({ url: state.url, requestedUrl: item.url, statusCode: response?.status(), title: state.title, forms: state.forms,
            hasPasswordField: state.password, hasUserField: state.user,
            observedLinks: policy.authenticationLinks(state.links, allowed),
            role: currentRole, discoveredLinks: state.links.length });
          row.status = 'authentication-required'; persist();
          const authenticated = await authenticate(state);
          if (!authenticated) break;
          state = authenticated;
          const unresolved = [...coverage.values()].filter(x => ['queued', 'authentication-required'].includes(x.status) && x.role !== currentRole);
          queue.length = 0;
          for (const x of unresolved) {
            x.status = 'requeued-after-authentication';
            if (!policy.isLogin({ url: x.url, title: '', password: false, user: false })) enqueue(x.url, x.discoveredFrom, x.label);
          }
        }
        const url = policy.normalizeUrl(state.url), finalKey = key(url);
        if (visited.has(finalKey)) { row.status = 'redirected'; row.finalUrl = url; continue; }
        const statusCode = requests.get(`${currentRole} GET ${url}`)?.statusCode ?? response?.status();
        visited.add(finalKey); row.status = statusCode >= 400 ? 'http-error' : 'visited'; row.finalUrl = url;
        const target = coverage.get(finalKey) || { url, role: currentRole, discoveredFrom: item.url };
        target.status = row.status; target.statusCode = statusCode; coverage.set(finalKey, target);
        if (statusCode >= 400) { errorCount++; target.reason = `HTTP ${statusCode}`; }
        pages.push({ url, requestedUrl: item.url, statusCode, title: state.title, forms: state.forms, role: currentRole, discoveredLinks: state.links.length });
        const r = requests.get(`${currentRole} GET ${url}`);
        if (r) { r.pageTitle = state.title; r.pageInspected = true; r.formCount = state.forms.length; r.formFieldNames = [...new Set(state.forms.flatMap(f => f.fields))].slice(0, 50); }
        for (const link of state.links) enqueue(link.url, url, link.label);
        if (formSubmissionLimit && !policy.isLogin(state)) await submitForms(state.forms, url);
        persist();
        process.stdout.write(`PAGE ${pages.length}/${maxPages} REQUESTS ${requests.size}/${maxRequests} QUEUED ${queue.length} ${displayUrl(url)}\n`);
      } catch (error) {
        errorCount++; row.status = 'error'; row.reason = String(error.message || error).split('\n')[0]; skipped.push({ url: item.url, reason: row.reason });
        process.stdout.write(`SKIP ${displayUrl(item.url)} ${row.reason}\n`);
        if (!pages.length && /ERR_PROXY_CONNECTION_FAILED|ERR_TUNNEL_CONNECTION_FAILED|ECONNREFUSED/i.test(row.reason)) { failureCode = 'proxy-unavailable'; break; }
      }
    }
    persist();
    const stoppedBy = authenticationBlocked ? 'authentication-required' : closeRequested ? 'cancelled' : Date.now() >= deadline ? 'time-limit' : pages.length >= maxPages ? 'page-limit' : requestCount >= maxRequests ? 'request-limit' : 'queue-empty';
    const status = policy.outcome({ pages: pages.length, authenticationBlocked, errors: errorCount, remaining: queue.length, limit: stoppedBy !== 'queue-empty' });
    const summary = { status, failureCode, startedAt, finishedAt: new Date(), visitedPageCount: pages.length, discoveredUrlCount: queued.size, observedRequestCount: requests.size, actualRequestCount: requestCount, remainingQueueCount: queue.length, skippedCount: skipped.length, stoppedBy, activeRole: currentRole, authenticationBlocked, formSubmissions, pages, skipped: skipped.slice(0, 1000) };
    atomic('fast-crawl-summary.json', summary);
    crawlComplete = true;
    if (interactive) { emit('CRAWL_DONE', { exitCode: failureCode || !pages.length ? 2 : 0 }); await closeSignal; }
    else { process.stdout.write(`${status.toUpperCase()} PAGES ${pages.length} REQUESTS ${requests.size}\n`); process.exitCode = failureCode || !pages.length ? 2 : 0; }
  } finally {
    if (context) persist();
    closing = true;
    const captureProgress = state => atomic('capture-progress.json', { ...state, updatedAt: new Date().toISOString() });
    captureProgress({ stage: 'settling' });
    if (ledger) await ledger.settle();
    captureProgress({ stage: 'browser-flush' });
    if (context) await context.close();
    if (browser) await browser.close();
    if (ledger) await ledger.finish();
    const pendingHar = path.join(runDirectory, 'fast-capture.har.pending');
    if (fs.existsSync(pendingHar)) {
      capture.restoreMissingHarEntries(runDirectory, pendingHar, allowed);
      const browserAudit = capture.auditHar(runDirectory, pendingHar, allowed);
      if (!browserAudit.complete) throw Error('Browser HAR and transaction journal differ; inspect capture-audit.json');
      await wireCapture.reconcile(runDirectory, allowed, pendingHar, captureProgress);
      captureProgress({ stage: 'auditing' });
      const audit = capture.auditHar(runDirectory, pendingHar, allowed);
      if (!audit.complete) throw Error('HAR and transaction journal differ; inspect capture-audit.json');
      fs.renameSync(pendingHar, path.join(runDirectory, 'fast-capture.har'));
    }
    captureProgress({ stage: 'complete' });
    input.close();
    if (interactive && fs.existsSync(path.join(runDirectory, 'browser-runtime.json'))) atomic('browser-runtime.json', { status: 'stopped' });
  }
}
main().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1; input.close(); });
