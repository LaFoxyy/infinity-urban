import{initShell,updateTimestamp,requestJson}from'./shell.js';
if(['#calendar','#reservations'].includes(location.hash)){location.replace('/reservas'+(location.hash==='#calendar'?'#calendario':'#lista'));}else{
try{
 await initShell();const token=(await requestJson('/api/auth/form')).csrfToken;const fetchOriginal=window.fetch.bind(window);
 // The approved UI keeps its requests; this adapter supplies the shared site's CSRF token.
 window.fetch=(input,init={})=>{const url=new URL(input instanceof Request?input.url:input,location.href);const method=(init.method||(input instanceof Request?input.method:'GET')).toUpperCase();if(url.origin===location.origin&&url.pathname.startsWith('/api/')&&!['GET','HEAD'].includes(method)){const headers=new Headers(init.headers||(input instanceof Request?input.headers:undefined));headers.set('X-CSRF-Token',token);init={...init,headers};}return fetchOriginal(input,init);};
 const status=await requestJson('/api/status');
 if(status.environment==='production'){const localize=()=>{const walker=document.createTreeWalker(document.querySelector('.operations'),NodeFilter.SHOW_TEXT);let text;while(text=walker.nextNode())if(/staging/i.test(text.nodeValue))text.nodeValue=text.nodeValue.replace(/staging/gi,'produção');};new MutationObserver(localize).observe(document.querySelector('.operations'),{childList:true,subtree:true});}
 document.getElementById('refresh').addEventListener('click',()=>document.getElementById('refresh-button').click());
 window.addEventListener('hashchange',()=>{if(location.hash==='#properties')location.assign('/acomodacoes');if(['#calendar','#reservations'].includes(location.hash))location.assign('/reservas'+(location.hash==='#calendar'?'#calendario':'#lista'));});
 await import('/operations-original.js');updateTimestamp(Date.now());
}catch(error){document.getElementById('main-content').textContent=error.message;}

}
