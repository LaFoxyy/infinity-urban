import {initLanguage} from './i18n.js';
import {icon,hydrateIcons} from './icons.js';
const $=id=>document.getElementById(id);
let csrf;
export function installNavigation(member){
  const items=[['/dashboard','grid','Dashboard'],['/relatorios','file','Relatórios'],['/acomodacoes','home','Acomodações'],['/proprietarios','users','Proprietários'],['/despesas-recorrentes','repeat','Despesas recorrentes'],['/designacoes','link','Designações'],['/gestao','shield','Gestão'],['/reservas#lista','file','Reservas'],['/mapeamento','link','Mapeamento Airbnb'],['/operacoes#connection','link','Channex & sync']];
  const active=path=>{const [url,hash]=path.split('#');return location.pathname===url&&(url==='/reservas'||!hash||(location.hash.slice(1)||'lista')===hash);};
  const nav=document.querySelector('.sidebar nav');if(!nav)return;
  nav.innerHTML=items.filter(([path])=>member&&(member.isRestrictedOwner?['/dashboard','/relatorios','/acomodacoes'].includes(path):!['/gestao','/designacoes'].includes(path)||member.canManage)).map(([path,symbol,label])=>`<a class="nav-item ${active(path)?'active':''}" href="${path}" title="${label}" ${active(path)?'aria-current="page"':''}>${icon(symbol)}<span>${member.isRestrictedOwner&&path==='/acomodacoes'?'Minhas acomodações':label}</span></a>`).join('');
}
export async function requestJson(path,body){
  if(body!==undefined&&!csrf)csrf=(await requestJson('/api/auth/form')).csrfToken;
  const response=await fetch(path,{method:body===undefined?'GET':'POST',credentials:'same-origin',cache:'no-store',headers:body===undefined?{}:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(body===undefined?60000:180000)});
  if(response.status===401){location.replace('/login');throw new Error('Sua sessão expirou.');}
  const data=await response.json().catch(()=>({}));
  if(!response.ok){if(response.status===400)csrf=undefined;throw new Error(data.error||`Não foi possível concluir a operação (${response.status}).`);}
  return data;
}
export async function uploadPhoto(file){
  if(!csrf)csrf=(await requestJson('/api/auth/form')).csrfToken;
  const form=new FormData();form.append('photo',file);
  const response=await fetch('/api/accommodations/photos',{method:'POST',credentials:'same-origin',headers:{'X-CSRF-Token':csrf},body:form,signal:AbortSignal.timeout(90000)});
  const data=await response.json().catch(()=>({}));
  if(!response.ok)throw new Error(data.error||'Não foi possível enviar a foto.');return data;
}
export async function initShell(){
  initLanguage();
  hydrateIcons();installNavigation();
  const data=await requestJson('/api/auth/session');const member=data.member;
  if(!member)throw new Error('Não foi possível verificar seu perfil.');installNavigation(member);
  $('account-name').textContent=member.fullName||member.email;$('account-email').textContent=member.email;
  $('account-role').textContent=member.role||'—';$('avatar').textContent=(member.fullName||member.email||'U').slice(0,1).toUpperCase();
  let theme='dark';try{theme=localStorage.getItem('urban-theme')==='light'?'light':'dark';}catch{}
  function applyTheme(){document.documentElement.dataset.theme=theme;$('brand-icon').src=`/assets/urban-infinity-icon-${theme}.png`;document.querySelector('link[rel=icon]').href=`/assets/urban-infinity-icon-${theme}.png`;$('theme-toggle').innerHTML=icon(theme==='dark'?'sun':'moon');$('theme-toggle').setAttribute('aria-label',theme==='dark'?'Ativar tema claro':'Ativar tema escuro');}
  applyTheme();$('theme-toggle').addEventListener('click',()=>{theme=theme==='dark'?'light':'dark';applyTheme();try{localStorage.setItem('urban-theme',theme);}catch{}});
  initLanguage();
  $('refresh').title=location.pathname==='/acomodacoes'?'Atualizar acomodações':'Atualizar operações';
  const mobile=matchMedia('(max-width:760px)');
  function close(){document.body.classList.remove('sidebar-open');$('sidebar-backdrop').hidden=true;$('sidebar').inert=mobile.matches;$('toggle-sidebar').setAttribute('aria-expanded',String(!mobile.matches&&!document.body.classList.contains('sidebar-collapsed')));}
  close();mobile.addEventListener('change',close);$('sidebar-backdrop').addEventListener('click',close);
  $('toggle-sidebar').addEventListener('click',()=>{if(mobile.matches){const open=document.body.classList.toggle('sidebar-open');$('sidebar').inert=!open;$('sidebar-backdrop').hidden=!open;$('toggle-sidebar').setAttribute('aria-expanded',String(open));}else{const collapsed=document.body.classList.toggle('sidebar-collapsed');$('toggle-sidebar').setAttribute('aria-expanded',String(!collapsed));}});
  $('account-toggle').addEventListener('click',()=>{$('account-popover').hidden=!$('account-popover').hidden;});
  $('notifications-toggle').addEventListener('click',()=>{$('notifications-popover').hidden=!$('notifications-popover').hidden;});
  $('logout').addEventListener('click',async()=>{await requestJson('/api/auth/logout',{});location.replace('/login');});
  document.addEventListener('keydown',event=>{if(event.key==='Escape'){close();$('account-popover').hidden=true;$('notifications-popover').hidden=true;}});
  document.addEventListener('click',event=>{if(!event.target.closest('.account-menu'))$('account-popover').hidden=true;if(!event.target.closest('#notifications-toggle')&&!event.target.closest('#notifications-popover'))$('notifications-popover').hidden=true;});
  return member;
}
export function updateTimestamp(date){document.getElementById('updated-at').textContent=new Intl.DateTimeFormat(document.documentElement.lang==='en'?'en-US':'pt-BR',{dateStyle:'short',timeStyle:'short',timeZone:'America/Sao_Paulo'}).format(new Date(date));}
export function node(tag,className,text,literal=false){const e=document.createElement(tag);if(className)e.className=className;if(text!==undefined)e.textContent=text;if(literal)e.dataset.i18nSkip='true';return e;}
