// Normal page viewing only. No guessed URLs, form submission, or vulnerability payloads.
const unsafe = /(?:logout|log-out|signout|sign-out|delete|remove|destroy|unsubscribe|checkout|purchase|payment|approve|reject|reset|cart\/add|action=(?:delete|remove|logout|purchase|approve|reject|reset))/i;
const unsafeLabel = /(?:ログアウト|削除|購入|決済|申請|承認|通知送信|リセット|\b(?:log\s*out|sign\s*out|delete|remove|reset|purchase|checkout|approve)\b)/i;
const asset = /\.(?:avif|bmp|css|csv|docx?|eot|gif|ico|jpe?g|js|mp3|mp4|mpeg|pdf|png|pptx?|svg|tar|tiff?|ttf|txt|wav|webm|webp|woff2?|xlsx?|xml|zip)$/i;
function normalizeUrl(value) { const u = new URL(value); u.hash = ''; if (!['http:', 'https:'].includes(u.protocol) || u.username || u.password) throw Error('Unsupported URL'); return u.href; }
function reason(value, label = '', extraForbidden = []) {
  let u; try { u = new URL(normalizeUrl(value)); } catch { return 'unsupported-url'; }
  const explicit = mutationReason(value, 'GET', extraForbidden);
  if (explicit) return explicit;
  if (matchesForbidden(label, extraForbidden)) return 'forbidden-action';
  let operation = u.pathname + u.search;
  try { operation = decodeURIComponent(operation); } catch { }
  if (unsafe.test(operation) || unsafeLabel.test(label)) return 'state-changing-link';
  if (asset.test(u.pathname)) return 'asset-or-download';
  return null;
}
// An in-origin link can explicitly point at an external redirect destination.
// Reject it before requesting the redirect endpoint, including protocol-relative
// destinations. This is a conservative preflight, not a redirect-response audit.
function externalNavigationTarget(value, origins) {
  const u=new URL(value);
  for(const text of u.searchParams.values()) {
    if(!/^(?:https?:)?\/\//i.test(text))continue;
    try { if(!origins.has(new URL(text,u).origin))return 'outside-origin-redirect-link'; } catch {}
  }
  return null;
}
function isTopLevelNavigation(request) {
  if(!request.isNavigationRequest())return false;
  try { const frame=request.frame();return frame===frame.page().mainFrame(); }
  catch { return true; } // Initial popup navigation has no frame yet; count it conservatively.
}
function authenticationLinks(links, allowed) {
  const result=[],seen=new Set();
  for(const link of links) {
    let target;try { target=new URL(link.url); } catch { continue; }
    if(!['http:','https:'].includes(target.protocol) || target.username || target.password || !allowed.has(target.origin) || externalNavigationTarget(target.href,allowed) || seen.has(target.href))continue;
    seen.add(target.href);
    const requiresUiLookup=!!target.search || target.hash.includes('?') || (!!target.hash && !/^#!?\//.test(target.hash));
    result.push({url:requiresUiLookup?null:target.href,label:String(link.label||'').slice(0,160),requiresUiLookup});
    if(result.length===50)break;
  }
  return result;
}
function isLogin(page) {
  const u=new URL(page.url), login=/\/(?:login|log-in|signin|sign-in|sign_in|logon|authenticate)(?:\.[a-z0-9]+)?\/?$/i;
  const hashRoute=/^#!?\//.test(u.hash) ? u.hash.replace(/^#!/,'').replace(/^#/,'').split('?')[0] : '';
  return login.test(u.pathname) || login.test(hashRoute) || /ログイン|サインイン|\blog[ -]?in\b|\bsign[ -]?in\b/i.test(page.title) || (page.password && page.user && !page.authenticatedIndicator);
}
// Serialized by Playwright. Observe controls, never their entered values.
function inspectDocument() {
  const visible=e=>!!e.getClientRects().length && getComputedStyle(e).visibility!=='hidden' && getComputedStyle(e).display!=='none';
  const identity=e=>[e.name,e.id,e.autocomplete,e.getAttribute('aria-label')].filter(Boolean).join(' ');
  const password=e=>e.type==='password' || /password|passwd|\bpwd\b/i.test(identity(e));
  const user=e=>!password(e) && (e.type==='email' || /user|email|login|account|identifier/i.test(identity(e)));
  const elements=f=>[...Object.getOwnPropertyDescriptor(HTMLFormElement.prototype,'elements').get.call(f)];
  // Include accessible names without ever observing editable input values.
  const label=e=>[e.textContent,e.getAttribute('aria-label'),e.getAttribute('title'),
    ...(e.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>e.getRootNode().getElementById?.(id)?.textContent),
    ...[...(e.labels||[])].map(l=>l.textContent),
    ...[...e.querySelectorAll('img[alt],[aria-label],[aria-labelledby]')].flatMap(c=>[c.getAttribute('alt'),c.getAttribute('aria-label'),...(c.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>c.getRootNode().getElementById?.(id)?.textContent)]),
    e.matches('input[type=submit],input[type=button],input[type=reset]')?e.value:''].filter(Boolean).join(' ');
  const forms=[...document.forms].map((f,formIndex)=>{
    const controls=elements(f),submitter=controls.find(e=>e.matches('button:not([type]),button[type=submit],input[type=submit]'));
    const formAction=new URL(f.getAttribute('action')||location.href,location.href).href,formMethod=(f.getAttribute('method')||'GET').toUpperCase();
    return {formIndex,method:(submitter?.hasAttribute('formmethod')?submitter.formMethod:formMethod).toUpperCase(),
    action:submitter?.hasAttribute('formaction')?submitter.formAction:formAction,
    formMethod,formAction,
    submitLabel:submitter?label(submitter):'',
    formLabel:[f.getAttribute('aria-label'),f.getAttribute('title'),...(f.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>f.getRootNode().getElementById?.(id)?.textContent)].filter(Boolean).join(' '),
    fields:controls.map(e=>e.name).filter(Boolean),hasPasswordField:controls.some(e=>password(e)&&visible(e)),
    hasFileField:controls.some(e=>(e.type||'').toLowerCase()==='file'),hasUserField:controls.some(e=>user(e)&&visible(e))};});
  const links=[...document.querySelectorAll('a[href]')].map(a=>({url:a.href,label:label(a).trim()}));
  const controls=[...document.querySelectorAll('a,button,[role="button"],[role="menuitem"],input[type="submit"]')].filter(visible);
  const logout=/ログアウト|\blog[ -]?out\b|\bsign[ -]?out\b/i;
  const authenticatedIndicator=controls.some(e=>logout.test([e.textContent,e.getAttribute('aria-label'),e.getAttribute('title'),e.getAttribute('value')].filter(Boolean).join(' '))) ||
    [...document.querySelectorAll('[aria-label="User menu"],[data-authenticated="true"]')].some(visible) ||
    [...document.forms].some(f=>visible(f)&&/\/(?:logout|log-out|signout|sign-out)(?:\/|\?|$)/i.test(f.action));
  const inputs=[...document.querySelectorAll('input')].filter(visible);
  return {url:location.href,title:document.title,forms,links,password:inputs.some(password),user:inputs.some(user),authenticatedIndicator};
}
function outcome({ pages, authenticationBlocked, errors, remaining, limit }) {
  if (!pages) return 'failed';
  if (authenticationBlocked || errors || remaining || limit) return 'partial';
  return 'completed';
}
// AI mode has no HTTP method denylist. Only explicit project prohibitions apply.
function mutationReason(value, method, forbidden = []) {
  let u;
  try { u = new URL(value); }
  catch { return 'unsupported-url'; }
  let text = u.pathname + u.search;
  try { text = decodeURIComponent(text); } catch { /* Match the literal URL if an escape is malformed. */ }
  text = u.origin + text;
  if (matchesForbidden(text, forbidden)) return 'forbidden-action';
  return null;
}
function matchesForbidden(text, forbidden = []) {
  return forbidden.some(x => typeof x === 'string' && x.trim() && String(text).toLowerCase().includes(x.trim().toLowerCase()));
}

function isLogout(value) {
  try {
    const u = new URL(value), pathname = decodeURIComponent(u.pathname);
    return /(?:^|\/)(?:logout|log[-_]out|signout|sign[-_]out)(?:\.[a-z0-9]+)?(?:\/|$)/i.test(pathname) ||
      [...u.searchParams].some(([k,v]) => /^(action|do|command)$/i.test(k) && /^(logout|log[-_]out|sign[-_]?out)$/i.test(v));
  } catch { return false; }
}

// Runs before site scripts in every document, including crawl/forms/authentication.
function installInteractionGuard(config) {
  const forbidden = (config.forbidden || []).filter(x=>typeof x==='string').map(x=>x.trim().toLowerCase()).filter(Boolean);
  if(!forbidden.length)return;
  const selector='button,a,input,select,textarea,[role=button],[role=link],[role=menuitem],[role=menuitemcheckbox],[role=menuitemradio],[role=checkbox],[role=radio],[role=switch],[role=option],[role=tab]';
  function label(control) {
    return [control.textContent,control.getAttribute('aria-label'),control.getAttribute('title'),
      ...(control.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>control.getRootNode().getElementById?.(id)?.textContent),
      ...[...(control.labels||[])].map(l=>l.textContent),
      ...[...control.querySelectorAll('img[alt],[aria-label],[aria-labelledby]')].flatMap(c=>[c.getAttribute('alt'),c.getAttribute('aria-label'),...(c.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>c.getRootNode().getElementById?.(id)?.textContent)]),
      control.matches('input[type=submit],input[type=button],input[type=reset]')?control.value:''].filter(Boolean).join(' ');
  }
  function destination(control) {
    const raw=control.matches('a[href]')?control.href:control.hasAttribute('formaction')?control.formAction:control.tagName==='FORM'?(control.getAttribute('action')||location.href):'';
    if(!raw)return '';
    try {const u=new URL(raw,location.href);return decodeURIComponent(u.pathname+u.search+u.hash);}catch{return '';}
  }
  function stop(event, label) {
    event.preventDefault(); event.stopImmediatePropagation();
    if(event.target instanceof HTMLInputElement && event.target.type==='file') event.target.value='';
    console.warn('[scopepilot-interaction-blocked] ' + label.slice(0, 160));
  }
  function blocked(text) { return forbidden.some(x => text.toLowerCase().includes(x)); }
  for (const type of ['pointerdown','pointerup','mousedown','mouseup','click','dblclick','auxclick','keydown','keypress','keyup','input','change','submit','dragstart','drop']) window.addEventListener(type, event => {
    if (type.startsWith('key') && !['Enter', ' '].includes(event.key)) return;
    const el = event.target instanceof Element ? event.target : null;
    if (!el) return;
    const controls=event.composedPath().filter(n=>n instanceof Element && n.matches(selector));
    const form=event.type==='submit'?el:type.startsWith('key')&&event.key==='Enter'?el.form:null;
    if(form) {
      const submitter=event.submitter || [...Object.getOwnPropertyDescriptor(HTMLFormElement.prototype,'elements').get.call(form)].find(e=>e.matches('button:not([type]),button[type=submit],input[type=submit]'));
      if(submitter)controls.push(submitter);
      // Do not mistake another submit button's label for the chosen action.
      if(blocked([form.getAttribute('aria-label'),form.getAttribute('title'),...(form.getAttribute('aria-labelledby')||'').split(/\s+/).filter(Boolean).map(id=>form.getRootNode().getElementById?.(id)?.textContent)].filter(Boolean).join(' ')) || blocked(destination(submitter?.hasAttribute('formaction')?submitter:form)))return stop(event,'forbidden-action');
    }
    for(const control of controls) if(blocked(label(control)) || blocked(destination(control)))return stop(event,'forbidden-action');
  }, true);
}
module.exports = { normalizeUrl, reason, externalNavigationTarget, isTopLevelNavigation, authenticationLinks, isLogin, inspectDocument, outcome, mutationReason, matchesForbidden, isLogout, installInteractionGuard };
