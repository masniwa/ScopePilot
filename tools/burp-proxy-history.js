'use strict';
const {spawn}=require('node:child_process');
const fs=require('node:fs');
const crypto=require('node:crypto');
function hash(value){return crypto.createHash('sha256').update(value).digest('hex');}
function fingerprint(url, wire){
  const boundary=wire.indexOf(Buffer.from('\r\n\r\n'));if(boundary<0)return null;
  const lines=wire.subarray(0,boundary).toString('latin1').split('\r\n');
  const method=lines.shift().split(' ')[0].toUpperCase(),headers=new Map();
  for(const line of lines){const i=line.indexOf(':');if(i<1)continue;const key=line.slice(0,i).toLowerCase();const value=line.slice(i+1).trim();headers.set(key,headers.has(key)?headers.get(key)+(key==='cookie'?'; ':', ')+value:value);}
  const parsed=new URL(url);parsed.hash='';
  return hash(method+'\n'+parsed.href+'\n'+hash(wire.subarray(boundary+4))+'\n'+(headers.get('cookie')||'')+'\n'+(headers.get('authorization')||''));
}
async function collect(config){
  if(!Array.isArray(config.fingerprints)||!Array.isArray(config.origins))throw Error('Invalid history lookup configuration');
  const origins=new Set(config.origins.map(x=>new URL(x).origin));
  const wanted=new Set(config.fingerprints.filter(x=>/^[0-9a-f]{64}$/.test(x)));
  const observations=(Array.isArray(config.observations)?config.observations:[]).filter(x=>wanted.has(x.fingerprint));
  const targets=observations.length?observations:[...wanted].map(fingerprint=>({fingerprint}));
  if(!wanted.size)return {status:'NO_CAPTURE_FINGERPRINTS',matches:[]};
  const child=spawn(config.command,config.args,{stdio:['pipe','pipe','pipe'],windowsHide:true});
  let sequence=0,buffer='',stderr='';const pending=new Map();
  child.stderr.on('data',c=>{stderr=(stderr+c.toString()).slice(-1000);});
  child.stdout.setEncoding('utf8');child.stdout.on('data',c=>{buffer+=c;let i;while((i=buffer.indexOf('\n'))>=0){const line=buffer.slice(0,i);buffer=buffer.slice(i+1);let msg;try{msg=JSON.parse(line);}catch{continue;}const p=pending.get(msg.id);if(p){pending.delete(msg.id);clearTimeout(p.timer);msg.error?p.reject(Error(msg.error.message||'MCP error')):p.resolve(msg.result);}}});
  child.on('error',e=>{for(const p of pending.values()){clearTimeout(p.timer);p.reject(e);}pending.clear();});
  child.on('exit',()=>{for(const p of pending.values()){clearTimeout(p.timer);p.reject(Error('Burp MCP connection closed'));}pending.clear();});
  function notify(method,params){child.stdin.write(JSON.stringify({jsonrpc:'2.0',method,params})+'\n');}
  function call(method,params){const id=++sequence;return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(id);reject(Error('Burp history lookup timed out'));},30000);pending.set(id,{resolve,reject,timer});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n');});}
  function texts(result){if(result?.isError)throw Error('Burp refused history lookup');return (result?.content||[]).filter(x=>x.type==='text').map(x=>x.text);}
  try{
    await call('initialize',{protocolVersion:'2024-11-05',capabilities:{},clientInfo:{name:'ScopePilot history reference',version:'0.14.0'}});notify('notifications/initialized',{});
    const listing=await call('tools/list',{});
    // Request-only metadata pages avoid scanning unrelated response bodies.
    // Host regex searches can exhaust the server's global read budget on large
    // PDF responses before reaching this engagement's requests.
    const unfiltered=listing.tools?.find(x=>x.name==='get_proxy_http_history'&&x.inputSchema?.properties?.includeRequestIds);
    const tool=unfiltered||listing.tools?.find(x=>x.name==='get_proxy_http_history_regex');
    if(!tool?.inputSchema?.properties?.includeRequestIds)return {status:'UNSUPPORTED_REQUEST_IDS',matches:[]};
    let captureId='';
    if(listing.tools.some(x=>x.name==='get_logger_history_status')){try{captureId=JSON.parse(texts(await call('tools/call',{name:'get_logger_history_status',arguments:{}}))[0]).captureId||'';}catch{}}
    const escape=x=>x.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
    const hosts=[...origins].map(x=>new URL(x).host).map(escape);
    if(!hosts.length)return {status:'NO_ALLOWED_ORIGINS',matches:[]};
    const regex='(?mi)^(?:Host|:authority):[ \\t]*(?:'+hosts.join('|')+')[ \\t]*\\r?$';
    const found=new Map();let pages=0;
    for(let offset=0;;offset+=100){
      const result=await call('tools/call',{name:tool.name,arguments:{count:100,offset,...(unfiltered?{}:{regex}),includeRequestIds:true}});
      const entries=[];for(const text of texts(result)){for(const part of text.split(/\n\s*\n/)){if(!part.trim())continue;try{const parsed=JSON.parse(part);entries.push(...(Array.isArray(parsed)?parsed:[parsed]));}catch{if(/denied|REGEX_|error/i.test(part))throw Error('Burp history lookup was denied or incomplete');}}
      }
      if(!entries.length)break;pages++;
      for(const entry of entries){
        if(entry.idSource!=='PROXY_UI'||!Number.isInteger(entry.requestId)||entry.requestId<0||typeof entry.requestBase64!=='string')throw Error('History API did not return native Proxy identifiers');
        if(!origins.has(new URL(entry.url).origin))continue;
        const key=fingerprint(entry.url,Buffer.from(entry.requestBase64,'base64'));
        if(wanted.has(key))for(let index=0;index<targets.length;index++){
          const target=targets[index];if(target.fingerprint!==key)continue;
          const requestTime=typeof entry.requestTime==='string'?entry.requestTime:null;
          const delta=Math.abs(Date.parse(requestTime)-Date.parse(target.observedAt));
          const distance=Number.isFinite(delta)?delta:Number.POSITIVE_INFINITY;
          const previous=found.get(index);
          if(!previous||distance<previous.distance||(distance===previous.distance&&entry.requestId<previous.requestId))
            found.set(index,{fingerprint:key,observationId:target.id,requestId:entry.requestId,captureId,requestTime,distance});
        }
      }
      // Identical requests can recur later in history. Continue to the end to
      // find the event nearest each observation, not the first matching row.
      if(entries.length<100)break;
    }
    return {status:'MATCHED',pages,matches:[...found.values()].map(({distance,...match})=>({...match,timeDeltaMs:Number.isFinite(distance)?distance:null}))};
  }finally{for(const p of pending.values())clearTimeout(p.timer);child.stdin.end();child.kill();}
}
module.exports={fingerprint,collect};
if(require.main===module)collect(JSON.parse(fs.readFileSync(process.argv[2],'utf8'))).then(result=>process.stdout.write(JSON.stringify(result)+'\n')).catch(()=>{process.stdout.write(JSON.stringify({status:'HISTORY_LOOKUP_FAILED',matches:[]})+'\n');process.exitCode=1;});
