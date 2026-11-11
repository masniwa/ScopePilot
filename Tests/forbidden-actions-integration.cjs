const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert'),{spawn}=require('child_process');
const root=process.argv[2],base=process.argv[3];
const {chromium}=require(path.join(root,'tools/playwright-runtime/node_modules/playwright'));
const policy=require(path.join(root,'tools/crawl-policy'));
const hits=[],cases=[];let origin,proxyUrl,current='',worker,browser;
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn,ms=25000){const end=Date.now()+ms;while(Date.now()<end){const v=fn();if(v)return v;await sleep(30)}throw Error('timeout');}
const forbidden=['Publish','/api/forbidden','/logout','Upload','削除','Delete','購入','決済'];
const site=http.createServer(async(req,res)=>{
  let body='';for await(const b of req)body+=b;
  hits.push({phase:current,method:req.method,url:req.url,body});
  if(process.argv.includes('--serve'))fs.appendFileSync(path.join(base,'received.jsonl'),JSON.stringify({method:req.method,url:req.url,body})+'\n');
  if(req.url==='/auth-ok'){res.writeHead(302,{'Set-Cookie':'session=ok; Path=/','Location':'/'});return res.end();}
  if(req.url==='/login'){res.setHeader('Content-Type','text/html');return res.end('<title>Login</title><form action="/auth-ok" method="post"><input name="username"><input type="password" name="password"><button>Login</button></form><button onclick="fetch(\'/api/action\',{method:\'POST\',body:\'auth-publish\'})">Publish</button>');}
  if(req.url.startsWith('/api/')){res.writeHead(200,{'Content-Type':'application/json'});return res.end('{"ok":true}');}
  if(req.url!=='/'){res.writeHead(404,{'Content-Length':'0'});return res.end();}
  res.setHeader('Content-Type','text/html; charset=utf-8');res.end(`<!doctype html><title>Ordinary editor</title>${req.headers.cookie?'<button>Logout</button>':''}
  <a href="/api/forbidden-crawl">Open endpoint</a><span id="publish-name">Publish</span>
  <a href="/should-not-navigate" aria-labelledby="publish-name">●</a>
  <button id="literal" onclick="send('literal')">Publish</button>
  <button id="labelled" aria-labelledby="publish-name" onclick="send('labelled')"><span>●</span></button>
  <div role="menuitem" aria-label="Publish" onclick="send('menu')"><span id="menu-icon">◆</span></div>
  <button id="alt" onclick="send('alt')"><img alt="Publish"></button>
  <button id="pointer" aria-label="Publish" onmousedown="send('pointer')">●</button>
  <button id="key" aria-label="Publish" onkeyup="if(event.key==='Enter')send('key')">●</button>
  <button id="japanese" onclick="send('japanese')">削除</button><button id="english" onclick="send('english')">Delete</button>
  <button id="purchase" onclick="send('purchase')">購入</button><button id="payment" onclick="send('payment')">決済</button>
  <input id="file" type="file" aria-label="Upload" onchange="send('file')">
  <label>Upload<input id="label-file" type="file" onchange="send('label-file')"></label>
  <input id="allowed-file" type="file" aria-label="Choose attachment" onchange="send('allowed-file')">
  <button id="allowed" onclick="send('allowed')">Save draft</button><div id="shadow"></div><iframe srcdoc="<button aria-label='Publish' onclick=&quot;fetch('${origin}/api/action',{method:'POST',body:'frame'})&quot;>●</button>"></iframe>
  <form method="post" action="/api/form/shared"><input name="name"><button>Publish</button></form>
  <form method="post" action="/api/form/shared"><input name="name"><button>Save draft</button></form>
  <form method="post" action="/api/forbidden-form"><button>Confirm</button></form>
  <form method="post" action="/api/base"><button formaction="/api/forbidden-override">Confirm</button></form>
  <form method="post" action="/api/forbidden-base"><button formaction="/api/override-ok">Confirm</button></form>
  <form method="post" action="/api/mixed"><button>Save draft</button><button>Publish</button></form>
  <form method="get" action="/api/method-ok"><button formmethod="post">Save draft</button></form>
  <form method="post" action="/api/named-controls"><input name="action"><input name="method"><input name="elements"><input name="requestSubmit"><button>Save draft</button></form>
  <form method="post" action="/api/form-name" aria-labelledby="publish-name"><button>Confirm</button></form>
  <form id="external-form" method="post" action="/api/external-form"></form><button form="external-form">Publish</button>
  <form method="post" action="/api/credentials"><input type="password" name="password"><button>Save credential</button></form>
  <script>function send(action){fetch('/api/action',{method:'POST',body:action})}const s=document.querySelector('#shadow').attachShadow({mode:'open'});s.innerHTML='<span id="name">Publish</span><button aria-labelledby="name">●</button>';s.querySelector('button').onclick=()=>send('shadow');fetch('/api/forbidden-auto',{method:'POST'}).catch(()=>{});</script>`);
});
const proxy=http.createServer((req,res)=>{const u=new URL(req.url);if(u.origin!==origin){res.writeHead(403);return res.end()}const up=http.request(u,{method:req.method,headers:req.headers},r=>{res.writeHead(r.statusCode,r.headers);r.pipe(res)});up.on('error',()=>{res.writeHead(502);res.end()});req.pipe(up)});
async function listen(s){await new Promise(r=>s.listen(0,'127.0.0.1',r));return 'http://127.0.0.1:'+s.address().port;}
async function phase(ai,forms,auth=false){
  current=auth?'authentication':`ai-${ai}-forms-${forms}`;
  const name=current,run=path.join(base,name);fs.mkdirSync(run,{recursive:true});
  fs.writeFileSync(path.join(run,'engagement.json'),JSON.stringify({StartUrl:origin+(auth?'/login':'/'),allowedOrigins:[origin],supportingOrigins:[],forbiddenActions:forbidden,aiInteractions:ai,crawlFormSubmissions:forms,deferLogoutUntilFinalization:ai,limits:{MaxPages:12,MaxRequests:200,MaxMinutes:3}}));
  fs.writeFileSync(path.join(run,'exploration-proxy.json'),JSON.stringify({url:proxyUrl}));
  worker=spawn(process.execPath,[path.join(root,'tools/fast-crawl.js'),run,'--interactive','--headless'],{stdio:['pipe','pipe','pipe'],windowsHide:true});let output='';worker.stdout.on('data',b=>output+=b);worker.stderr.on('data',b=>output+=b);
  const received=()=>hits.filter(h=>h.phase===name);
  const connect=async()=>{browser=await chromium.connectOverCDP(JSON.parse(fs.readFileSync(path.join(run,'browser-runtime.json'))).cdpEndpoint);return browser.contexts()[0].pages()[0];};
  try{
    if(auth){
      await until(()=>fs.existsSync(path.join(run,'human-intervention.json')));const page=await connect();
      await page.getByRole('button',{name:'Publish',exact:true}).click();
      await page.evaluate(async()=>{await fetch('/api/forbidden-auth',{method:'POST'}).catch(()=>{});});
      assert.ok(!received().some(h=>h.body==='auth-publish'||h.url==='/api/forbidden-auth'));
      await page.locator('[name=username]').fill('fixture-user');await page.locator('[name=password]').fill('fixture-password');await page.getByRole('button',{name:'Login',exact:true}).click();await page.waitForURL(origin+'/');
      const intervention=JSON.parse(fs.readFileSync(path.join(run,'human-intervention.json')));fs.writeFileSync(path.join(run,'resume.signal'),JSON.stringify({requestId:intervention.requestId,activeRole:'User'}));
    }
    await until(()=>output.includes('CRAWL_DONE')||worker.exitCode!==null);assert.ok(output.includes('CRAWL_DONE'),output);
    assert.ok(!received().some(h=>h.url.startsWith('/api/forbidden')||h.url==='/should-not-navigate'),JSON.stringify(received()));
    const formPosts=received().filter(h=>h.method==='POST'&&h.url.startsWith('/api/')&&h.url!=='/api/action');
    assert.deepEqual(formPosts.map(h=>h.url).sort(),forms?['/api/form/shared','/api/method-ok','/api/mixed','/api/named-controls','/api/override-ok']:[]);
    if(!browser)await connect();const page=browser.contexts()[0].pages()[0];await page.goto(origin+'/');
    const before=received().filter(h=>h.url==='/api/action').length;
    for(const id of ['literal','labelled','menu-icon','alt','pointer','japanese','english','purchase','payment'])await page.locator('#'+id).click();
    await page.locator('#key').press('Enter');await page.locator('#shadow button').click();await page.frames().find(f=>f!==page.mainFrame()).getByRole('button').click();
    await page.evaluate(()=>{
      document.body.insertAdjacentHTML('beforeend','<form id="key-form" onsubmit="event.preventDefault()"><input id="key-input"><button>Publish</button></form>');
      document.querySelector('#key-input').onkeyup=e=>{if(e.key==='Enter')send('key-form');};
    });
    await page.locator('#key-input').press('Enter');
    const upload=path.join(run,'fixture.txt');fs.writeFileSync(upload,'ScopePilot inert input');
    for(const id of ['file','label-file']){await page.locator('#'+id).setInputFiles(upload);assert.equal(await page.locator('#'+id).inputValue(),'');}
    await sleep(100);assert.equal(received().filter(h=>h.url==='/api/action').length,before,'forbidden UI must not send');
    if(ai){
      fs.writeFileSync(path.join(run,'exploration-phase.json'),JSON.stringify({stage:'finalization',allowLogout:true}));
      await page.evaluate(async()=>{await fetch('/logout',{method:'POST'}).catch(()=>{});});
      assert.ok(!received().some(h=>h.url==='/logout'),'finalization does not override explicit prohibitions');
      await page.locator('#allowed').click();await page.locator('#allowed-file').setInputFiles(upload);await until(()=>received().some(h=>h.body==='allowed')&&received().some(h=>h.body==='allowed-file'));
      for(const method of ['GET','HEAD','POST','PUT','PATCH','DELETE','OPTIONS','PROPFIND'])await page.evaluate(async method=>{await fetch('/api/forbidden-method',{method}).catch(()=>{});await fetch('/api/allowed-method',{method}).catch(()=>{});},method);
      assert.equal(received().filter(h=>h.url==='/api/allowed-method').length,8,'allowed methods remain available');
    }
    assert.ok(!received().some(h=>h.url.startsWith('/api/forbidden')),'explicit URLs must never reach receiver');
    const log=fs.readFileSync(path.join(run,'blocked-actions.jsonl'),'utf8');assert.ok(log.includes('forbidden-action'));
    const phaseSummary=JSON.parse(fs.readFileSync(path.join(run,'fast-crawl-summary.json')));
    if(forms)assert.ok(phaseSummary.formSubmissions.skipped.filter(x=>x.reason==='forbidden-action').length>=5,JSON.stringify(phaseSummary.formSubmissions));
    cases.push({name,forbiddenReceived:0,allowedFormPosts:formPosts.length,uiBlocked:true,authAllowed:auth,allowedAiMethods:ai?8:0});
  }finally{
    if(browser){await browser.close();browser=null;}
    if(worker){const closing=worker;const exited=new Promise(r=>closing.once('exit',r));if(closing.exitCode===null){closing.stdin.write('close\n');await exited;}worker=null;assert.equal(closing.exitCode,0,output);}
    fs.writeFileSync(path.join(run,'worker-console.log'),output);
    const audit=JSON.parse(fs.readFileSync(path.join(run,'capture-audit.json')));assert.ok(audit.complete,'blocked attempts and allowed traffic must remain in HAR/journal');
  }
}
(async()=>{origin=await listen(site);proxyUrl=await listen(proxy);
  if(process.argv.includes('--serve')) {
    fs.mkdirSync(base,{recursive:true});
    fs.writeFileSync(path.join(base,'fixture.json'),JSON.stringify({origin,proxyUrl}));console.log('FIXTURE_READY '+origin);
    process.stdin.resume();await new Promise(resolve=>process.stdin.once('data',resolve));return;
  }
  for(const method of ['GET','POST','PROPFIND'])assert.equal(policy.mutationReason('https://example.test/api/private',method,['https://example.test/api/private']),'forbidden-action');
  assert.equal(policy.reason('https://example.test/api/private','Open',['/api/private']),'forbidden-action');
  assert.equal(policy.mutationReason('https://example.test/items','DELETE',[]),null);
  assert.equal(policy.reason('https://example.test/search?q=100%','Open',[]),null);
  assert.equal(policy.mutationReason('https://example.test/api/%70rivate','GET',['/api/private']),'forbidden-action');
  assert.equal(policy.matchesForbidden('購入',['購入・決済']),false,'entries stay literal, not undocumented alternatives');
  for(const ai of [false,true])for(const forms of [false,true])await phase(ai,forms);
  await phase(true,false,true);
  fs.writeFileSync(path.join(base,'verification.json'),JSON.stringify({cases,hits},null,2));console.log(JSON.stringify({passed:true,cases}));
})().catch(e=>{fs.writeFileSync(path.join(base,'failure.json'),JSON.stringify({error:e.stack,cases,hits},null,2));console.error(e.stack);process.exitCode=1}).finally(async()=>{if(browser)await browser.close();if(worker)worker.kill();site.close();proxy.close();});
