// Append-only evidence for every in-scope transaction, including repeated requests/failures.
// Bodies and headers are local evidence and can contain credentials. Never send them to the AI.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
function attach(context, directory, allowed, role) {
  const ids = new WeakMap(), active = new Set(), tasks = new Set();
  let sequence = 0, responses = 0, failed = 0, bodyErrors = 0, requestErrors = 0;
  const bodies = path.join(directory, 'http-bodies'); fs.mkdirSync(bodies, { recursive: true });
  const journal = path.join(directory, 'http-transactions.jsonl');
  const append = value => fs.appendFileSync(journal, JSON.stringify(value) + '\n');
  context.on('request', request => {
    let origin; try { origin = new URL(request.url()).origin; } catch { return; }
    if (!allowed.has(origin)) return;
    const id = ++sequence; ids.set(request,id); active.add(id);
    const body = request.postDataBuffer();
    append({kind:'request',id,at:new Date().toISOString(),method:request.method(),url:request.url(),headers:request.headers(),postDataAvailable:body !== null,postDataBase64:body?.toString('base64') || '',role:role()});
    // request.headers() intentionally omits some security headers; retain the complete set separately.
    const headersTask = request.allHeaders().then(headers => {
      const bodyExpectedBytes = Number(headers['content-length'] || 0);
      const bodyMissing = bodyExpectedBytes > 0 && (body === null || body.length !== bodyExpectedBytes);
      if (bodyMissing) requestErrors++;
      append({kind:'request-details',id,headers,bodyMissing,bodyExpectedBytes,bodyCapturedBytes:body?.length ?? 0});
    }).catch(error => {requestErrors++;append({kind:'capture-error',id,error:String(error.message || error)});});
    tasks.add(headersTask); headersTask.finally(()=>tasks.delete(headersTask)).catch(()=>{});
  });
  context.on('response', response => {
    const id = ids.get(response.request()); if (!id) return;
    responses++;
    const task = (async()=>{
      const record = {kind:'response',id,at:new Date().toISOString(),status:response.status(),headers:await response.allHeaders()};
      try {
        const bodyless = response.request().method() === 'HEAD' || [204,304].includes(record.status) || record.headers['content-length'] === '0';
        const body = bodyless ? Buffer.alloc(0) : await response.body();
        const file = String(id).padStart(8,'0') + '.bin';
        fs.writeFileSync(path.join(bodies,file),body);
        Object.assign(record,{body:'http-bodies/'+file,bytes:body.length,sha256:crypto.createHash('sha256').update(body).digest('hex')});
      } catch(error) { bodyErrors++; record.bodyError=String(error.message || error); }
      append(record);
    })();
    tasks.add(task); task.catch(error=>{bodyErrors++;append({kind:'capture-error',id,error:String(error.message || error)});}).finally(()=>tasks.delete(task)).catch(()=>{});
  });
  context.on('requestfinished', request => {
    const id=ids.get(request); if(!id)return;active.delete(id);append({kind:'finished',id,at:new Date().toISOString()});
  });
  context.on('requestfailed', request => {
    const id=ids.get(request);if(!id)return;failed++;active.delete(id);append({kind:'failed',id,at:new Date().toISOString(),error:request.failure()?.errorText || 'unknown'});
  });
  return {
    async settle(milliseconds=30000) {
      const deadline=Date.now()+milliseconds;
      while((active.size || tasks.size) && Date.now()<deadline)await new Promise(r=>setTimeout(r,100));
    },
    async finish() {
      await Promise.allSettled([...tasks]);
      const summary={requests:sequence,responses,failed,bodyErrors,requestErrors,unresolvedIds:[...active],complete:active.size===0 && bodyErrors===0 && requestErrors===0,finishedAt:new Date().toISOString()};
      fs.writeFileSync(path.join(directory,'capture-summary.json'),JSON.stringify(summary,null,2));return summary;
    }
  };
}
function auditHar(directory, harPath, allowed) {
  const rows = fs.readFileSync(path.join(directory,'http-transactions.jsonl'),'utf8').split('\n').filter(Boolean).map(JSON.parse).filter(r=>r.kind==='request');
  const har=JSON.parse(fs.readFileSync(harPath,'utf8'));
  const entries=har.log.entries.filter(e=>allowed.has(new URL(e.request.url).origin));
  const groups=new Map();
  const key = r=>JSON.stringify([r.method.toUpperCase(),r.url]);
  entries.forEach((entry,index)=>{const k=key(entry.request);if(!groups.has(k))groups.set(k,[]);groups.get(k).push(index);});
  const missingIds=[];
  for(const row of rows){const indexes=groups.get(key(row));if(indexes?.length)indexes.shift();else missingIds.push(row.id);}
  const extraHarEntries=[...groups.values()].flat();
  const audit={transactions:rows.length,harRequests:entries.length,matched:rows.length-missingIds.length,missingIds,extraHarEntries,complete:missingIds.length===0 && extraHarEntries.length===0};
  fs.writeFileSync(path.join(directory,'capture-audit.json'),JSON.stringify(audit,null,2));return audit;
}
// Chromium can omit an in-flight request when the context closes. Retain the
// browser event as an attempt; only NetLog can establish that it was sent.
// Do not invent a response, failure or completion for an unresolved attempt.
function restoreMissingHarEntries(directory, harPath, allowed) {
  const rows=fs.readFileSync(path.join(directory,'http-transactions.jsonl'),'utf8').split('\n').filter(Boolean).map(JSON.parse);
  const har=JSON.parse(fs.readFileSync(harPath,'utf8')), queues=new Map();
  const key=r=>JSON.stringify([r.method.toUpperCase(),r.url]);
  for(const entry of har.log.entries){const k=key(entry.request);queues.set(k,(queues.get(k)||0)+1);}
  const added=[];
  for(const request of rows.filter(r=>r.kind==='request' && allowed.has(new URL(r.url).origin))){
    const k=key(request), count=queues.get(k)||0;
    if(count){queues.set(k,count-1);continue;}
    const related=rows.filter(r=>r.id===request.id), details=related.find(r=>r.kind==='request-details');
    const response=related.find(r=>r.kind==='response'), failure=related.find(r=>r.kind==='failed');
    const headers=value=>Object.entries(value||{}).map(([name,value])=>({name,value:String(value)}));
    const body=Buffer.from(request.postDataBase64||'','base64');
    const content={size:response?.bytes??-1,mimeType:response?.headers?.['content-type']||''};
    if(response?.body){content.text=fs.readFileSync(path.join(directory,response.body)).toString('base64');content.encoding='base64';}
    if(response?.bodyError)content._scopepilotBodyError=response.bodyError;
    const entry={startedDateTime:request.at,time:-1,
      request:{method:request.method,url:request.url,httpVersion:'',cookies:[],headers:headers(details?.headers||request.headers),queryString:[...new URL(request.url).searchParams].map(([name,value])=>({name,value})),headersSize:-1,bodySize:details?.bodyMissing?-1:body.length,...(request.postDataAvailable?{postData:{mimeType:(details?.headers||request.headers)?.['content-type']||'',text:body.toString('utf8'),_scopepilotBase64:body.toString('base64')}}:{})},
      response:{status:response?.status??-1,statusText:'',httpVersion:'',cookies:[],headers:headers(response?.headers),content,redirectURL:response?.headers?.location||'',headersSize:-1,bodySize:response?.bytes??-1,...(failure?{_failureText:failure.error}:{})},
      cache:{},timings:{send:-1,wait:-1,receive:-1},
      _scopepilotJournal:{id:request.id,source:'PlaywrightBrowserEvent',transmissionInferred:false,terminalRecorded:related.some(r=>['finished','failed'].includes(r.kind))}};
    har.log.entries.push(entry);added.push(request.id);
  }
  if(added.length){
    fs.copyFileSync(harPath,path.join(directory,'browser-capture.original.har'));
    fs.writeFileSync(harPath,JSON.stringify(har));
  }
  fs.writeFileSync(path.join(directory,'browser-har-restoration.json'),JSON.stringify({addedIds:added,source:'http-transactions.jsonl',transmissionInferred:false},null,2));
  return added;
}
module.exports={attach,auditHar,restoreMissingHarEntries};
