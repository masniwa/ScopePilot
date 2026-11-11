const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const [root, base] = process.argv.slice(2);
const { chromium } = require(path.join(root, 'tools/playwright-runtime/node_modules/playwright'));
const policy = require(path.join(root, 'tools/crawl-policy'));
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn) { for (let n = 0; n < 300; n++) { const v = fn(); if (v) return v; await sleep(100); } throw Error('Timed out'); }
async function listen(server) { await new Promise(r => server.listen(0, '127.0.0.1', r)); return `http://127.0.0.1:${server.address().port}`; }
const received = [], bodies = [];
function previewPdf() {
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Kids [3 0 R] /Count 1 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>','<< /Length 0 >>\nstream\n\nendstream'];
  let pdf = '%PDF-1.4\n'; const offsets = [0];
  for (let i = 0; i < objects.length; i++) { offsets.push(Buffer.byteLength(pdf)); pdf += `${i+1} 0 obj\n${objects[i]}\nendobj\n`; }
  const start = Buffer.byteLength(pdf);
  pdf += `xref\n0 5\n0000000000 65535 f \n${offsets.slice(1).map(x=>String(x).padStart(10,'0')+' 00000 n \n').join('')}trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n${start}\n%%EOF\n`;
  return Buffer.from(pdf);
}
let origin, outside, worker, mcp, browser;
const site = http.createServer(async (req, res) => {
  received.push(req.method + ' ' + req.url);
  if (process.argv.includes('--serve')) fs.appendFileSync(path.join(base,'received.jsonl'),JSON.stringify({method:req.method,url:req.url})+'\n');
  if (req.method !== 'GET') {
    let body = ''; for await (const c of req) body += c;
    bodies.push({ method: req.method, body });
    res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ saved: body, method: req.method })); return;
  }
  if (['/robots.txt', '/sitemap.xml', '/favicon.ico'].includes(req.url)) { res.writeHead(404, {'Content-Length':'0'}); return res.end(); }
  if (['/preview', '/download'].includes(req.url)) {
    res.setHeader('Content-Type', 'application/pdf');
    if (req.url === '/download') res.setHeader('Content-Disposition', 'attachment; filename="preview.pdf"');
    return res.end(previewPdf());
  }
  if(req.url.startsWith('/worker-start.js?')){res.writeHead(302,{Location:'/worker-final.js'+new URL(req.url,'http://fixture').search+'#redirect-view','Content-Length':'0'});return res.end();}
  if(req.url.startsWith('/worker-final.js?')){res.writeHead(200,{'Content-Type':'text/javascript'});return res.end('postMessage("worker-ready");');}
  res.setHeader('Content-Type', 'text/html');
  res.end(`<!doctype html><title>Item editor</title><a href="/preview">Preview PDF</a><a href="/download">Download PDF</a><label>Name <input aria-label="Name" id="name"></label>
    <button onclick="save('POST')">Create</button><button onclick="save('PUT')">Update</button><button onclick="save('PATCH')">Amend</button>
    <button onclick="save('DELETE')">Delete</button><button onclick="save('POST')">Publish</button>
    <button onclick="fetch('/api/forbidden',{method:'POST'})">Forbidden endpoint</button>
    <button onclick="fetch('${outside}/outside',{method:'POST'})">Outside origin</button>
    <form action="/login" method="post" onsubmit="event.preventDefault();fetch('/login',{method:'POST',body:'fixture-login'})"><input type="password" name="password"><button>Login</button></form>
    <button onclick="fetch('/logout',{method:'POST'})">Logout</button>
    <input id="file" type="file" hidden onchange="const f=new FormData();f.append('document',this.files[0]);fetch('/api/upload',{method:'POST',body:f})"><button onclick="document.querySelector('#file').click()">Upload fixture</button>
    <div id="hover-pad" onmouseenter="document.querySelector('#hover-save').hidden=false">Hover actions<button hidden id="hover-save" onclick="fetch('/api/hover',{method:'POST',body:'ScopePilot-hover'})">Hover save</button></div>
    <div id="drag-one" draggable="true" style="width:120px;height:55px;background:#ccc" ondragstart="event.dataTransfer.setData('text/plain','ScopePilot-drag')">Drag card</div>
    <div id="drop-area" style="width:120px;height:55px;background:#eee;margin:35px" ondragover="event.preventDefault()" ondrop="event.preventDefault();fetch('/api/drag',{method:'POST',body:event.dataTransfer.getData('text/plain')})">Drop here</div>
    <div id="step-drag" style="width:120px;height:45px;background:#ddd" onmousedown="window.stepped={x:event.clientX,y:event.clientY,active:false,dropped:false}">Threshold card</div><div id="step-drop" style="width:120px;height:45px;background:#eee;margin-left:200px">Threshold destination</div>
    <button onclick="let n=0;for(let i=0;i<3;i++){let w=new Worker('/worker-start.js?variant=a&literal=%23value#client-view',{type:'module'});w.onmessage=()=>{document.querySelector('#worker-result').textContent='worker-ready:'+ ++n}}">Worker redirects</button><output id="worker-result"></output>
    <div role="status" id="result"></div><script>async function save(method){const r=await fetch('/api/items',{method,headers:{'Content-Type':'application/json'},body:JSON.stringify({name:document.querySelector('#name').value})}); document.querySelector('#result').textContent=await r.text();}
    document.addEventListener('mousemove',e=>{const s=window.stepped;if(!s)return;if(!s.active){if(Math.hypot(e.clientX-s.x,e.clientY-s.y)>5)s.active=true;return}const b=document.querySelector('#step-drop').getBoundingClientRect();if(e.clientX>b.x&&e.clientX<b.right&&e.clientY>b.y&&e.clientY<b.bottom)s.dropped=true});document.addEventListener('mouseup',()=>{const s=window.stepped;window.stepped=null;if(s?.dropped)fetch('/api/step-drag',{method:'POST',body:'ScopePilot-stepped-drag'})});</script>`);
});
const externalHits = [];
const external = http.createServer((req, res) => { externalHits.push(req.url); res.end('outside'); });
const proxyHits = [];
const proxy = http.createServer((req, res) => {
  const u = new URL(req.url); proxyHits.push(req.method + ' ' + u.pathname);
  if (u.origin !== origin) { res.writeHead(403); return res.end(); }
  const forward = http.request(u, { method: req.method, headers: req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
  forward.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(forward);
});
async function phase(enabled, proxyUrl) {
  const run = path.join(base, enabled ? 'interactive' : 'readonly'); fs.mkdirSync(run, { recursive: true });
  fs.writeFileSync(path.join(run, 'engagement.json'), JSON.stringify({ StartUrl: origin + '/', allowedOrigins: [origin], supportingOrigins: [], forbiddenActions: ['Publish','/api/forbidden'], aiInteractions: enabled, limits: { MaxPages: 10, MaxRequests: 100, MaxMinutes: 3 } }));
  const contract = JSON.parse(fs.readFileSync(path.join(run, 'engagement.json')));
  contract.deferLogoutUntilFinalization = enabled;
  fs.writeFileSync(path.join(run, 'engagement.json'), JSON.stringify(contract));
  const phaseFile=path.join(run,'exploration-phase.json');
  fs.writeFileSync(phaseFile,JSON.stringify({stage:'review',allowLogout:false}));
  fs.writeFileSync(path.join(run, 'exploration-proxy.json'), JSON.stringify({ url: proxyUrl }));
  worker = spawn(process.execPath, [path.join(root, 'tools/fast-crawl.js'), run, '--interactive', '--headless'], { stdio: ['pipe', 'pipe', 'pipe'] });
  let output = ''; worker.stdout.on('data', c => { output += c; }); worker.stderr.on('data', c => { output += c; });
  try {await until(() => output.includes('CRAWL_DONE') || worker.exitCode !== null);}
  catch(error){throw Error(error.message+'\n'+output);}
  assert.ok(output.includes('CRAWL_DONE'), output);
  browser = await chromium.connectOverCDP(JSON.parse(fs.readFileSync(path.join(run, 'browser-runtime.json'))).cdpEndpoint);
  const page = browser.contexts()[0].pages()[0];
  assert.ok(received.includes('GET /preview') && received.includes('GET /download'), 'crawl must finish after PDF preview and download');
  await page.goto(origin + '/', {waitUntil:'domcontentloaded'});
  const cli = path.join(root, 'tools/playwright-runtime/node_modules/@playwright/mcp/cli.js');
  const tools = ['browser_snapshot', 'browser_navigate', 'browser_network_requests', ...(enabled ? ['browser_click', 'browser_fill_form', 'browser_type', 'browser_select_option', 'browser_press_key', 'browser_file_upload','browser_hover','browser_drag','browser_mouse_drag_xy','browser_mouse_move_xy','browser_mouse_down','browser_mouse_up'] : [])];
  const uploadRoot=path.join(run,'upload-fixtures');fs.mkdirSync(uploadRoot,{recursive:true});const upload=path.join(uploadRoot,'sample.pdf');fs.writeFileSync(upload,'ScopePilot-local-fixture-upload');
  mcp = spawn(process.execPath, [path.join(root, 'tools/mcp-tool-filter.js'), '--allow', tools.join(','), '--upload-root', uploadRoot, '--', process.execPath, cli, '--cdp-endpoint', JSON.parse(fs.readFileSync(path.join(run, 'browser-runtime.json'))).cdpEndpoint,'--caps','vision'], { stdio: ['pipe', 'pipe', 'pipe'] });
  const replies = new Map(); let id = 0;
  readline.createInterface({ input: mcp.stdout }).on('line', line => { try { const j = JSON.parse(line); replies.set(j.id, j); } catch {} });
  mcp.stderr.on('data', c => { output += c; });
  async function call(method, params) { const next = ++id; mcp.stdin.write(JSON.stringify({ jsonrpc:'2.0',id:next,method,params })+'\n'); return await until(() => replies.get(next)); }
  async function tool(name, args = {}) { const r = await call('tools/call', { name, arguments:args }); assert.ok(!r.error && !r.result?.isError, JSON.stringify(r)); return r.result.content.filter(c => c.type === 'text').map(c => c.text).join('\n'); }
  await call('initialize', { protocolVersion:'2024-11-05',capabilities:{},clientInfo:{name:'nonget-regression',version:'1'} });
  mcp.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
  const listed = (await call('tools/list', {})).result.tools.map(t => t.name);
  assert.equal(listed.includes('browser_click'), enabled);
  assert.ok(!listed.includes('browser_run_code'));assert.equal(listed.includes('browser_file_upload'),enabled);
  if (!enabled) {
    assert.ok((await call('tools/call', { name:'browser_click', arguments:{ref:'absent'} })).error);
    await page.getByRole('button', { name:'Create', exact:true }).click(); await sleep(200);
    assert.ok(!received.includes('POST /api/items'), 'read-only mode must block POST');
  } else {
    const snapshot = await tool('browser_snapshot');
    function ref(text, role) { const match = snapshot.match(new RegExp(role + ' "' + text + '"[^\\n]*\\[ref=([^\\]]+)\\]')); assert.ok(match, snapshot); return match[1]; }
    await tool('browser_fill_form', {fields:[{name:'Name',type:'textbox',ref:ref('Name','textbox'),target:ref('Name','textbox'),value:'ScopePilot-local-fixture'}]});
    for (const [name, method] of [['Create','POST'],['Update','PUT'],['Amend','PATCH'],['Delete','DELETE']]) {
      await tool('browser_click', {element:name,ref:ref(name,'button'),target:ref(name,'button')}); await until(() => received.includes(method+' /api/items'));
    }
    assert.ok(bodies.every(b => b.body.includes('ScopePilot-local-fixture')), JSON.stringify(bodies));
    await tool('browser_click', {element:'Create',ref:ref('Create','button'),target:ref('Create','button')});
    await until(()=>bodies.filter(b=>b.method==='POST').length>=2);
    const before = bodies.length;
    for (const name of ['Publish','Forbidden endpoint','Outside origin']) {
      await tool('browser_click', {element:name,ref:ref(name,'button'),target:ref(name,'button')});
    }
    await sleep(200);
    assert.equal(bodies.length,before,'blocked actions must not reach fixture');
    assert.deepEqual(externalHits,[]);
    const network = await tool('browser_network_requests'); assert.ok(network.includes('POST') && network.includes('PATCH'),network);
    // The project prohibition also applies when an application fires fetch without a click.
    await page.evaluate(async () => { await fetch('/api/forbidden',{method:'PATCH'}).catch(()=>{}); });
    await sleep(100); assert.equal(bodies.length,before);
    assert.equal(policy.mutationReason(origin+'/api/items','POST'),null);
    assert.equal(policy.mutationReason(origin+'/api/items','DELETE'),null);
    assert.equal(policy.mutationReason(origin+'/api/items','PROPFIND'),null);
    assert.ok(fs.existsSync(path.join(run,'blocked-actions.jsonl')));
    await tool('browser_click',{element:'Logout',ref:ref('Logout','button'),target:ref('Logout','button')});
    await page.evaluate(async()=>{await fetch('/logout').catch(()=>{});});
    assert.ok(!received.some(x=>/^(POST|GET) \/logout$/.test(x)),'known logout must not reach application while reviewing ordinary operations');
    fs.unlinkSync(phaseFile);
    await page.evaluate(async()=>{await fetch('/logout',{method:'POST'}).catch(()=>{});});
    assert.ok(!received.includes('POST /logout'),'missing phase state must fail closed');
    await tool('browser_click',{element:'Update after deferred logout',ref:ref('Update','button'),target:ref('Update','button')});
    await until(()=>received.filter(x=>x==='PUT /api/items').length>=2);
    assert.ok(fs.readFileSync(path.join(run,'blocked-requests.jsonl'),'utf8').includes('logout-deferred-until-finalization'));
    fs.writeFileSync(phaseFile,JSON.stringify({stage:'finalization',allowLogout:true}));
    for (const name of ['Login','Logout']) await tool('browser_click', {element:name,ref:ref(name,'button'),target:ref(name,'button')});
    await until(()=>received.includes('POST /login') && received.includes('POST /logout'));
    await page.evaluate(async () => { await fetch('/logout'); await fetch('/api/items',{method:'PROPFIND',body:'ScopePilot-custom-method'}); });
    assert.ok(received.includes('GET /logout'));
    assert.ok(received.includes('PROPFIND /api/items'));
    const outsideFile=path.join(run,'outside-upload.txt');fs.writeFileSync(outsideFile,'ScopePilot-should-not-upload');
    assert.ok((await call('tools/call',{name:'browser_file_upload',arguments:{paths:[outsideFile]}})).error,'outside-root upload must not be forwarded');
    await tool('browser_click',{target:ref('Upload fixture','button')});
    await tool('browser_file_upload',{paths:[upload]});
    await until(()=>received.includes('POST /api/upload'));
    assert.ok(bodies.some(b=>b.body.includes('ScopePilot-local-fixture-upload')),'multipart bytes must reach fixture');
    assert.ok(listed.includes('browser_hover')&&listed.includes('browser_drag')&&listed.includes('browser_mouse_drag_xy'));
    await tool('browser_hover',{target:'#hover-pad'});await tool('browser_snapshot');
    await tool('browser_click',{target:'#hover-save'});await until(()=>received.includes('POST /api/hover'));
    await tool('browser_drag',{startTarget:'#drag-one',endTarget:'#drop-area'});await until(()=>received.includes('POST /api/drag'));
    const start=await page.locator('#drag-one').boundingBox(),end=await page.locator('#drop-area').boundingBox();
    await tool('browser_mouse_drag_xy',{startX:start.x+12,startY:start.y+12,endX:end.x+30,endY:end.y+20});
    await until(()=>received.filter(x=>x==='POST /api/drag').length===2);
    await page.locator('#step-drag').scrollIntoViewIfNeeded();
    const steppedStart=await page.locator('#step-drag').boundingBox(),steppedEnd=await page.locator('#step-drop').boundingBox();
    await tool('browser_mouse_move_xy',{x:steppedStart.x+10,y:steppedStart.y+10});
    await tool('browser_mouse_down');
    await tool('browser_mouse_move_xy',{x:steppedStart.x+18,y:steppedStart.y+10});
    await tool('browser_mouse_move_xy',{x:steppedEnd.x+30,y:steppedEnd.y+20});
    await tool('browser_mouse_up');await until(()=>received.includes('POST /api/step-drag'));
    await tool('browser_click',{target:'button:has-text("Worker redirects")'});
    await page.locator('#worker-result').filter({hasText:'worker-ready:3'}).waitFor();
    // Request arrival and a UI callback do not prove every response body has
    // reached the browser. Keep this complete-capture scenario alive until its
    // finite fixture requests have settled before closing the CDP connection.
    await page.waitForLoadState('networkidle',{timeout:10000});
  }
  mcp.kill(); mcp = null; await browser.close(); browser=null;
  worker.stdin.write('close\n'); await new Promise(r=>worker.once('exit',r)); worker=null;
  const har = JSON.parse(fs.readFileSync(path.join(run,'fast-capture.har')));
  const transactions = fs.readFileSync(path.join(run,'http-transactions.jsonl'),'utf8').trim().split('\n').map(JSON.parse);
  const rawRequests = transactions.filter(e=>e.kind==='request');
  for(const url of ['/robots.txt','/sitemap.xml']) assert.ok(rawRequests.some(e=>e.url===origin+url),'discovery HTTP must be captured: '+url);
  const scopedHar = har.log.entries.filter(e=>new URL(e.request.url).origin===origin);
  assert.equal(rawRequests.length,scopedHar.length,'journal and HAR must retain every occurrence');
  assert.ok(JSON.parse(fs.readFileSync(path.join(run,'capture-audit.json'))).complete,'per-method/URL multisets must match');
  const captureSummary=JSON.parse(fs.readFileSync(path.join(run,'capture-summary.json')));
  const bodyErrors=transactions.filter(e=>e.kind==='wire-response'&&e.bodyError).map(e=>({id:e.id,status:e.status,bytes:e.bytes,expected:e.headers?.['content-length'],path:new URL(rawRequests.find(r=>r.id===e.id).url).pathname,error:e.bodyError}));
  assert.ok(captureSummary.complete,'capture must be finalized: '+JSON.stringify({summary:captureSummary,bodyErrors}));
  if(enabled){
    const source=origin+'/worker-start.js?variant=a&literal=%23value',destination=origin+'/worker-final.js?variant=a&literal=%23value';
    assert.equal(received.filter(x=>x==='GET /worker-final.js?variant=a&literal=%23value').length,3,'fixture must see every repeated redirect hop');
    assert.equal(rawRequests.filter(x=>x.url===destination).length,3,'all actual redirect requests must be in the journal');
    const redirected=scopedHar.filter(e=>e.request.url===destination);assert.equal(redirected.length,3);
    for(const entry of redirected){assert.equal(entry.response.status,200);assert.equal(Buffer.from(entry.response.content.text,'base64').toString(),'postMessage("worker-ready");');}
    assert.equal(scopedHar.filter(e=>e.request.url===source&&e.response.status===302).length,3,'preserve actual redirect status and full query');
    assert.ok(rawRequests.every(r=>!new URL(r.url).hash),'URL fragments are never HTTP request targets');
    assert.equal(JSON.parse(fs.readFileSync(path.join(run,'wire-capture-summary.json'))).addedRequests,3);
  }
  if(enabled)assert.equal(rawRequests.filter(e=>e.method==='POST' && e.url===origin+'/api/items').length,2,'repeated POSTs must not collapse');
  if(enabled) {
    const uploadRequest=rawRequests.find(e=>e.method==='POST' && e.url===origin+'/api/upload');
    assert.ok(uploadRequest,'multipart upload must be recorded');
    assert.ok(Buffer.from(uploadRequest.postDataBase64,'base64').toString().includes('ScopePilot-local-fixture-upload'),'journal must retain actual uploaded fixture bytes');
  }
  if (enabled) for (const method of ['POST','PUT','PATCH','DELETE']) {
    assert.ok(har.log.entries.some(e=>e.request.method===method && e.response.status===200 && e.request.postData.text.includes('ScopePilot-local-fixture')),method+' HAR request/response');
    assert.ok(proxyHits.includes(method+' /api/items'),method+' must traverse proxy');
    const observations = fs.readFileSync(path.join(run,'observed-requests.jsonl'),'utf8').trim().split('\n').map(JSON.parse);
    assert.ok(observations.some(e=>e.method===method && e.statusCode===200),method+' observations');
  }
  console.log('PASS '+(enabled?'MCP fill/click sends POST/PUT/PATCH/DELETE and login/logout through proxy; custom method allowed; HAR bodies/responses and observations saved; explicit project prohibitions and outside origin blocked':'read-only tools and POST block preserved'));
}
(async()=>{
  outside=await listen(external);origin=await listen(site);const proxyUrl=await listen(proxy);
  if (process.argv.includes('--serve')) {
    fs.mkdirSync(base,{recursive:true});fs.writeFileSync(path.join(base,'fixture.json'),JSON.stringify({origin,proxyUrl}));
    await new Promise(resolve=>{process.stdin.on('data',()=>resolve());setTimeout(resolve,240000).unref();});
  } else {
    await phase(false,proxyUrl);await phase(true,proxyUrl);
    // Equal counts alone are insufficient: one dropped POST plus an extra GET must fail.
    const auditDir=path.join(base,'negative-audit');fs.mkdirSync(auditDir,{recursive:true});
    fs.writeFileSync(path.join(auditDir,'http-transactions.jsonl'),[
      {kind:'request',id:1,method:'POST',url:origin+'/api/items'},
      {kind:'request',id:2,method:'POST',url:origin+'/api/items'}
    ].map(JSON.stringify).join('\n'));
    const auditPath=path.join(auditDir,'fake.har');
    fs.writeFileSync(auditPath,JSON.stringify({log:{entries:[
      {request:{method:'POST',url:origin+'/api/items'}},
      {request:{method:'GET',url:origin+'/api/items'}}
    ]}}));
    const audit=require(path.join(root,'tools/capture-ledger')).auditHar(auditDir,auditPath,new Set([origin]));
    assert.equal(audit.complete,false);assert.deepEqual(audit.missingIds,[2]);assert.deepEqual(audit.extraHarEntries,[1]);
    console.log('PASS journal/HAR audit detects equal-count substitution');
  }
})()
  .catch(e=>{console.error(e.stack);process.exitCode=1;})
  .finally(async()=>{if(mcp)mcp.kill();if(browser)await browser.close().catch(()=>{});if(worker)worker.kill();site.closeAllConnections();external.closeAllConnections();proxy.closeAllConnections();site.close();external.close();proxy.close();if(process.argv.includes('--serve'))process.stdin.destroy();});
