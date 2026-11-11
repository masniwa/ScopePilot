const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const root=path.resolve(process.argv[2]),base=path.resolve(process.argv[3]);
const {chromium}=require(path.join(root,'tools/playwright-runtime/node_modules/playwright'));
fs.mkdirSync(base,{recursive:true});
let origin,proxyUrl,phase='',worker,browser;const hits=[],results=[];
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn,ms=25000){const end=Date.now()+ms;while(Date.now()<end){const value=fn();if(value)return value;await sleep(30)}throw Error('timeout');}
const site=http.createServer(async(req,res)=>{
 let body='';for await(const chunk of req)body+=chunk;
 hits.push({phase,method:req.method,url:req.url,body});
 if(req.url.startsWith('/save/')||req.url==='/touched-context'){res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});return res.end('<p>Saved fixture</p>');}
 if(req.url!=='/'){res.writeHead(404,{'Content-Length':'0'});return res.end();}
 res.setHeader('Content-Type','text/html; charset=utf-8');res.end(`<!doctype html><title>Ordinary form editor</title>
 <form method="post" action="/save/typed-url"><input name="target" type="url" value="${origin}/existing.svg"><button>Save</button></form>
 <form method="post" action="/save/camel-url"><input name="imageUrl"><button>Save</button></form>
 <form method="post" action="/save/lowercase-url"><input name="imageurl"><button>Save</button></form>
 <form method="post" action="/save/label-url"><label>Image URL<input name="opaque"></label><button>Save</button></form>
 <span id="resource-label">Resource URI</span><form method="post" action="/save/aria-url"><input name="unknown" aria-labelledby="resource-label"><button>Save</button></form>
 <form method="post" action="/save/placeholder-url"><input name="address" placeholder="https://example.invalid/image.svg"><button>Save</button></form>
 <form method="post" action="/save/existing-url"><input name="avatar_url" value="${origin}/existing.svg"><button>Save</button></form>
 <form method="post" action="/save/textarea-uri"><textarea name="callbackUri"></textarea><button>Save</button></form>
 <form method="post" action="/save/autocomplete-url"><input name="opaque2" autocomplete="url"><button>Save</button></form>
 <form id="external" method="post" action="/save/external-url"><input name="note" value="preserve me" oninput="fetch('/touched-context',{method:'POST',body:this.value})"><button>Save</button></form><input form="external" name="resourceURL">
 <form method="post" action="/save/readonly-url"><input name="profileLink" readonly value="${origin}/existing.svg"><button>Save</button></form>
 <form method="post" action="/save/plain"><label>Comments about the website<textarea name="comment"></textarea></label><input name="name"><input type="email" name="email"><input type="number" name="amount"><button>Save</button></form>
 <form method="post" action="/save/hidden"><input type="hidden" name="url" value="/fixed-return"><input name="note"><input disabled name="imageUrl" value="disabled unchanged"><button>Save</button></form>`);
});
const proxy=http.createServer((req,res)=>{const url=new URL(req.url);if(url.origin!==origin){res.writeHead(403);return res.end();}const upstream=http.request(url,{method:req.method,headers:req.headers},response=>{res.writeHead(response.statusCode,response.headers);response.pipe(res);});upstream.on('error',()=>{res.writeHead(502);res.end();});req.pipe(upstream);});
async function listen(server){await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));return 'http://127.0.0.1:'+server.address().port;}
async function runCase(ai){
 phase='ai-'+ai;const run=path.join(base,phase);fs.mkdirSync(run,{recursive:true});
 fs.writeFileSync(path.join(run,'engagement.json'),JSON.stringify({StartUrl:origin+'/',allowedOrigins:[origin],supportingOrigins:[],forbiddenActions:[],aiInteractions:ai,crawlFormSubmissions:true,limits:{MaxPages:20,MaxRequests:300,MaxMinutes:2}}));
 fs.writeFileSync(path.join(run,'exploration-proxy.json'),JSON.stringify({url:proxyUrl}));
 worker=spawn(process.execPath,[path.join(root,'tools/fast-crawl.js'),run,'--interactive','--headless'],{stdio:['pipe','pipe','pipe'],windowsHide:true});let output='';
 worker.stdout.on('data',b=>output+=b);worker.stderr.on('data',b=>output+=b);
 try{
  await until(()=>output.includes('CRAWL_DONE')||worker.exitCode!==null);assert.ok(output.includes('CRAWL_DONE'),output);
  browser=await chromium.connectOverCDP(JSON.parse(fs.readFileSync(path.join(run,'browser-runtime.json'),'utf8')).cdpEndpoint);
  const page=browser.contexts()[0].pages()[0],received=hits.filter(x=>x.phase===phase);
  const sends=received.filter(x=>x.method==='POST');
  const summary=JSON.parse(fs.readFileSync(path.join(run,'fast-crawl-summary.json'),'utf8'));
  const values=await page.evaluate(()=>[...document.forms].map(f=>({action:new URL(f.action).pathname,fields:[...f.elements].filter(x=>x.name).map(x=>({name:x.name,value:x.value}))})));
  results.push({phase,sends,formSubmissions:summary.formSubmissions,values});
  fs.writeFileSync(path.join(base,'results.json'),JSON.stringify({origin,results,hits},null,2));
  assert.deepEqual(sends.map(x=>x.url).sort(),['/save/hidden','/save/plain'],'URL-context forms must send zero requests; ordinary forms must still submit');
  assert.equal(new URLSearchParams(sends.find(x=>x.url==='/save/plain').body).get('comment'),'ScopePilot確認用入力');
  assert.equal(new URLSearchParams(sends.find(x=>x.url==='/save/hidden').body).get('url'),'/fixed-return');
  assert.equal(received.filter(x=>x.url==='/touched-context').length,0,'Defer the whole form before changing any controls');
  const skipped=summary.formSubmissions.skipped.filter(x=>x.reason==='contextual-url-input');
  assert.equal(skipped.length,11);
  assert.equal(values.find(x=>x.action==='/save/existing-url').fields[0].value,origin+'/existing.svg');
  assert.equal(values.find(x=>x.action==='/save/external-url').fields.find(x=>x.name==='note').value,'preserve me');
 } finally {
  fs.writeFileSync(path.join(run,'worker.log'),output);
  if(browser){await browser.close().catch(()=>{});browser=null;}
  if(worker&&worker.exitCode===null){worker.stdin.write('close\n');await until(()=>worker.exitCode!==null,60000);}
 }
}
(async()=>{origin=await listen(site);proxyUrl=await listen(proxy);for(const ai of [false,true])await runCase(ai);console.log(JSON.stringify({passed:true,cases:results.length,deferredUrlFormsPerCase:11,ordinaryFormsPerCase:2}));})().catch(error=>{console.error(error.stack);process.exitCode=1;}).finally(async()=>{if(worker&&worker.exitCode===null)worker.kill();if(browser)await browser.close().catch(()=>{});site.closeAllConnections?.();proxy.closeAllConnections?.();await Promise.all([new Promise(r=>site.close(r)),new Promise(r=>proxy.close(r))]);});
