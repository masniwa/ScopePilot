const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const base=process.argv[2],root=process.argv[3],{reconcile}=require(path.join(base,'tools','wire-capture.js'));
const epoch=1000000,url='http://fixture.test/api/items/',stamp=t=>new Date(epoch+t).toISOString();
const types={URL_REQUEST_START_JOB:1,HTTP_TRANSACTION_SEND_REQUEST_HEADERS:2,HTTP_TRANSACTION_READ_RESPONSE_HEADERS:3,URL_REQUEST_JOB_FILTERED_BYTES_READ:4,REQUEST_ALIVE:5,CANCELLED:6};
const event=(type,id,time,params={},phase=0)=>({type:types[type],source:{id},time:String(time),params,phase});
async function scenario(order){
 const dir=path.join(root,'response-vs-aborted-'+order.join('-'));fs.mkdirSync(path.join(dir,'http-bodies'),{recursive:true});
 const records={1:[{kind:'request',id:1,at:stamp(0),method:'GET',url,headers:{}},{kind:'failed',id:1,at:stamp(300),error:'net::ERR_ABORTED'}],2:[{kind:'request',id:2,at:stamp(200),method:'GET',url,headers:{}},{kind:'response',id:2,at:stamp(8000),status:403,bodyError:'Navigated away before response.body()'},{kind:'finished',id:2,at:stamp(8002)}]};
 fs.writeFileSync(path.join(dir,'http-transactions.jsonl'),order.flatMap(id=>records[id]).map(JSON.stringify).join('\n')+'\n');
 const trace=[event('URL_REQUEST_START_JOB',10,2500,{url,method:'GET'},1),event('HTTP_TRANSACTION_SEND_REQUEST_HEADERS',10,2700,{line:'GET /api/items/ HTTP/1.1',headers:[]}),event('HTTP_TRANSACTION_READ_RESPONSE_HEADERS',10,8000,{headers:['HTTP/1.1 403 Forbidden','Content-Length: 6']}),event('URL_REQUEST_JOB_FILTERED_BYTES_READ',10,8001,{byte_count:6,bytes:Buffer.from('denied').toString('base64')}),event('REQUEST_ALIVE',10,8002,{},2),event('URL_REQUEST_START_JOB',20,10500,{url,method:'GET'},1),event('HTTP_TRANSACTION_SEND_REQUEST_HEADERS',20,10800,{line:'GET /api/items/ HTTP/1.1',headers:[]}),event('CANCELLED',20,10830),event('REQUEST_ALIVE',20,10831,{},2)];
 const header=JSON.stringify({constants:{logEventTypes:types,timeTickOffset:String(epoch)}}).slice(0,-1)+',';
 fs.writeFileSync(path.join(dir,'chromium-netlog.json'),header+'\n"events": [\n'+trace.map(JSON.stringify).join(',\n')+'\n]}\n');
 const entries={1:{startedDateTime:stamp(0),time:300,request:{method:'GET',url},response:{status:-1,content:{size:0}}},2:{startedDateTime:stamp(200),time:7802,request:{method:'GET',url},response:{status:403,content:{size:0}}}};
 const har=path.join(dir,'capture.har');fs.writeFileSync(har,JSON.stringify({log:{entries:order.map(id=>entries[id])}}));
 const result=await reconcile(dir,new Set(['http://fixture.test']),har),rows=fs.readFileSync(path.join(dir,'http-transactions.jsonl'),'utf8').trim().split('\n').map(JSON.parse),summary=JSON.parse(fs.readFileSync(path.join(dir,'capture-summary.json')));
 assert.equal(summary.bodyErrors,0,'a cancelled wire occurrence must not displace the response-bearing match');
 assert.equal(summary.unresolvedIds.length,0);assert.ok(summary.complete);assert.equal(result.wireRequests,2);assert.equal(result.matchedBrowserRequests,1);assert.equal(result.addedRequests,1);
 const response=rows.find(x=>x.kind==='wire-response'&&x.id===2);assert.equal(response.status,403);assert.equal(fs.readFileSync(path.join(dir,response.body),'utf8'),'denied');
 assert.ok(!rows.some(x=>x.kind==='wire-request'&&x.id===1),'the earlier aborted browser event was never sent on this wire trace');
 assert.ok(rows.some(x=>x.kind==='wire-request'&&x.sourceId===20),'the unmatched cancelled wire occurrence must still be retained');
}
async function queuedRequest(){
 const dir=path.join(root,'queued-request');fs.mkdirSync(path.join(dir,'http-bodies'),{recursive:true});
 const rows=[{kind:'request',id:1,at:stamp(0),method:'GET',url,headers:{}},{kind:'request',id:2,at:stamp(12000),method:'GET',url,headers:{}},{kind:'failed',id:2,at:stamp(12500),error:'net::ERR_ABORTED'}];
 const trace=[event('URL_REQUEST_START_JOB',10,100,{url,method:'GET'},1),event('HTTP_TRANSACTION_SEND_REQUEST_HEADERS',10,8100,{line:'GET /api/items/ HTTP/1.1',headers:[]}),event('CANCELLED',10,8200),event('REQUEST_ALIVE',10,8201,{},2)];
 const header=JSON.stringify({constants:{logEventTypes:types,timeTickOffset:String(epoch)}}).slice(0,-1)+',';
 fs.writeFileSync(path.join(dir,'chromium-netlog.json'),header+'\n"events": [\n'+trace.map(JSON.stringify).join(',\n')+'\n]}\n');
 fs.writeFileSync(path.join(dir,'http-transactions.jsonl'),rows.map(JSON.stringify).join('\n')+'\n');
 const har=path.join(dir,'capture.har');fs.writeFileSync(har,JSON.stringify({log:{entries:rows.filter(r=>r.kind==='request').map(r=>({startedDateTime:r.at,time:0,request:{method:'GET',url},response:{status:-1,content:{size:0}}}))}}));
 const result=await reconcile(dir,new Set(['http://fixture.test']),har),after=fs.readFileSync(path.join(dir,'http-transactions.jsonl'),'utf8').trim().split('\n').map(JSON.parse),wire=after.find(r=>r.kind==='wire-request');
 assert.equal(wire.id,1,'a delayed header send belongs to the request created before its connection wait');assert.equal(wire.requestedAt,stamp(100));assert.equal(wire.at,stamp(8100));
 assert.equal(result.addedRequests,0);assert.equal(result.matchedBrowserRequests,1);assert.equal(JSON.parse(fs.readFileSync(path.join(dir,'capture-summary.json'))).unresolvedIds.length,0);
}
async function cancelledWithoutTransmission(ambiguous){
 const dir=path.join(root,'cancelled-before-send-'+ambiguous);fs.mkdirSync(path.join(dir,'http-bodies'),{recursive:true});
 const rows=[{kind:'request',id:1,at:stamp(0),method:'GET',url,headers:{}},...(ambiguous?[{kind:'request',id:2,at:stamp(50),method:'GET',url,headers:{}}]:[])];
 const trace=[event('URL_REQUEST_START_JOB',10,100,{url,method:'GET'},1),event('CANCELLED',10,8000),event('REQUEST_ALIVE',10,8001,{},2)];
 const header=JSON.stringify({constants:{logEventTypes:types,timeTickOffset:String(epoch)}}).slice(0,-1)+',';
 fs.writeFileSync(path.join(dir,'chromium-netlog.json'),header+'\n"events": [\n'+trace.map(JSON.stringify).join(',\n')+'\n]}\n');
 fs.writeFileSync(path.join(dir,'http-transactions.jsonl'),rows.map(JSON.stringify).join('\n')+'\n');
 const har=path.join(dir,'capture.har');fs.writeFileSync(har,JSON.stringify({log:{entries:rows.map(r=>({startedDateTime:r.at,time:0,request:{method:'GET',url},response:{status:-1,content:{size:0}}}))}}));
 const result=await reconcile(dir,new Set(['http://fixture.test']),har),after=fs.readFileSync(path.join(dir,'http-transactions.jsonl'),'utf8').trim().split('\n').map(JSON.parse),summary=JSON.parse(fs.readFileSync(path.join(dir,'capture-summary.json')));
 assert.equal(result.wireRequests,0);assert.equal(result.addedRequests,0);assert.ok(!after.some(r=>r.kind==='wire-request'||r.kind==='response'),'a cancelled browser attempt must not invent a transmission or response');
 assert.equal(result.cancelledBeforeTransmission,ambiguous?0:1);assert.equal(summary.unresolvedIds.length,ambiguous?2:0);assert.equal(summary.complete,!ambiguous);
 if(!ambiguous)assert.ok(after.some(r=>r.kind==='failed'&&r.id===1&&r.cancelledBeforeSend&&r.wireSourceId===10));
}
async function responseLengthCase(label,method,status,encoding,expected,text,incomplete){
 const dir=path.join(root,'response-length-'+label);fs.mkdirSync(path.join(dir,'http-bodies'),{recursive:true});
 const request={kind:'request',id:1,at:stamp(0),method,url,headers:{}};
 fs.writeFileSync(path.join(dir,'http-transactions.jsonl'),JSON.stringify(request)+'\n');
 const headers=[`HTTP/1.1 ${status} Result`,`Content-Length: ${expected}`,...(encoding?[`Content-Encoding: ${encoding}`]:[])];
 const trace=[event('URL_REQUEST_START_JOB',10,50,{url,method},1),event('HTTP_TRANSACTION_SEND_REQUEST_HEADERS',10,100,{line:method+' /api/items/ HTTP/1.1',headers:[]}),event('HTTP_TRANSACTION_READ_RESPONSE_HEADERS',10,200,{headers}),...(text?[event('URL_REQUEST_JOB_FILTERED_BYTES_READ',10,250,{byte_count:Buffer.byteLength(text),bytes:Buffer.from(text).toString('base64')})]:[]),event('REQUEST_ALIVE',10,300,{},2)];
 const header=JSON.stringify({constants:{logEventTypes:types,timeTickOffset:String(epoch)}}).slice(0,-1)+',';
 fs.writeFileSync(path.join(dir,'chromium-netlog.json'),header+'\n"events": [\n'+trace.map(JSON.stringify).join(',\n')+'\n]}\n');
 const har=path.join(dir,'capture.har');fs.writeFileSync(har,JSON.stringify({log:{entries:[{startedDateTime:request.at,time:300,request:{method,url},response:{status,content:{size:0}}}]}}));
 const result=await reconcile(dir,new Set(['http://fixture.test']),har),summary=JSON.parse(fs.readFileSync(path.join(dir,'capture-summary.json'))),response=JSON.parse(fs.readFileSync(har)).log.entries[0].response;
 assert.equal(summary.bodyErrors,incomplete?1:0);assert.equal(summary.complete,!incomplete);assert.equal(result.complete,!incomplete);
 assert.equal(!!response.content._scopepilotBodyError,incomplete,'HAR must keep the partial-body warning even when captured bytes are available');
 assert.equal(Buffer.from(response.content.text||'',response.content.encoding||'utf8').toString(),text,'received bytes remain available');
}
function missingBrowserHar(){
 const {restoreMissingHarEntries,auditHar}=require(path.join(base,'tools','capture-ledger.js'));
 const dir=path.join(root,'missing-browser-har');fs.mkdirSync(path.join(dir,'http-bodies'),{recursive:true});
 const rows=[{kind:'request',id:1,at:stamp(0),method:'GET',url,headers:{}},{kind:'failed',id:1,at:stamp(1),error:'blocked'},
 {kind:'request',id:2,at:stamp(2),method:'GET',url,headers:{}},
 {kind:'request',id:3,at:stamp(3),method:'POST',url,headers:{'content-type':'application/json'},postDataAvailable:true,postDataBase64:Buffer.from('{"n":1}').toString('base64')},
 {kind:'response',id:3,at:stamp(4),status:201,headers:{'content-type':'text/plain'},body:'http-bodies/3.bin',bytes:2},{kind:'finished',id:3,at:stamp(5)}];
 fs.writeFileSync(path.join(dir,'http-bodies/3.bin'),'ok');
 fs.writeFileSync(path.join(dir,'http-transactions.jsonl'),rows.map(JSON.stringify).join('\n')+'\n');
 const har=path.join(dir,'capture.har'),original=JSON.stringify({log:{entries:[{request:{method:'GET',url},response:{status:-1}}]}});fs.writeFileSync(har,original);
 assert.deepEqual(restoreMissingHarEntries(dir,har,new Set(['http://fixture.test'])),[2,3]);
 assert.equal(fs.readFileSync(path.join(dir,'browser-capture.original.har'),'utf8'),original);
 const entries=JSON.parse(fs.readFileSync(har)).log.entries;
 assert.equal(entries.length,3);assert.equal(entries[1].response.status,-1);assert.ok(!entries[1].response._failureText,'unresolved attempt must not invent a failure');
 assert.equal(entries[1]._scopepilotJournal.transmissionInferred,false);assert.equal(entries[1]._scopepilotJournal.terminalRecorded,false);
 assert.equal(entries[2].response.status,201);assert.equal(Buffer.from(entries[2].response.content.text,'base64').toString(),'ok');
 assert.equal(entries[2].request.postData.text,'{"n":1}');
 assert.equal(auditHar(dir,har,new Set(['http://fixture.test'])).complete,true);
 assert.deepEqual(restoreMissingHarEntries(dir,har,new Set(['http://fixture.test'])),[]);
 const extra=JSON.parse(fs.readFileSync(har));extra.log.entries.push({request:{method:'DELETE',url},response:{status:204}});fs.writeFileSync(har,JSON.stringify(extra));
 restoreMissingHarEntries(dir,har,new Set(['http://fixture.test']));assert.equal(auditHar(dir,har,new Set(['http://fixture.test'])).complete,false,'unknown extra evidence must still fail the audit');
}

(async()=>{missingBrowserHar();await scenario([1,2]);await scenario([2,1]);await queuedRequest();await cancelledWithoutTransmission(false);await cancelledWithoutTransmission(true);await responseLengthCase('partial','GET',200,null,6,'abc',true);await responseLengthCase('decoded-gzip','GET',200,'gzip',3,'abcdef',false);await responseLengthCase('head','HEAD',200,null,6,'',false);await responseLengthCase('no-content','GET',204,null,6,'',false);console.log('PASS response ownership, queued timing, conservative cancellation, partial-body preservation and decoded/empty-body exceptions');})().catch(e=>{console.error(e.stack||e);process.exitCode=1});
