import { hydrateIcons, icon } from './icons.js';
import {installNavigation} from './shell.js';

const $ = id => document.getElementById(id);
const copy = {
  'pt-BR': { title:'Gestão',subtitle:'Vincule apartamentos a proprietários e gerencie contas (Master/Admin)',pending:'Pendentes',approved:'Aprovados',rejected:'Rejeitados',all:'Todos',search:'Buscar por nome ou email...',allRoles:'Todas as roles',noRole:'Sem role',name:'Nome',email:'Email',status:'Status',role:'Role',actions:'Ações',empty:'Nenhum usuário',loading:'Carregando usuários…',approve:'Aprovar',reject:'Rejeitar',remove:'Excluir conta',cancel:'Cancelar',confirm:'Confirmar',saving:'Salvando…',retry:'Tentar novamente',updated:'Última atualização: ',readOnly:'Você está em modo de consulta. Somente Master pode alterar contas e permissões.',self:'Você não pode alterar sua própria conta.',multi:'Múltiplas funções; permissões preservadas.',logout:'Sair da conta',missingName:'Sem nome',success:'Alteração salva.',deleted:'Conta excluída.',deleteTitle:'Excluir conta permanentemente',deleteDescription:email=>`A conta de ${email} será excluída do Supabase, incluindo seu acesso ao site atual. Esta ação não pode ser desfeita.`,typeEmail:'Digite o email da conta para confirmar',wrongEmail:'Digite exatamente o email da conta.',statusTitle:action=>action==='approved'?'Aprovar conta':'Rejeitar conta',statusDescription:(name,status)=>status==='approved'?`Aprovar o acesso de ${name}? O status também será alterado no site atual.`:`Rejeitar o acesso de ${name}? A conta deixará de acessar este portal e o site atual.`,roleTitle:'Alterar função',roleDescription:(name,role)=>`Alterar a função de ${name} para ${role}? Isso também altera as permissões no site atual.`,unavailable:name=>`${name} será implementado em uma próxima etapa.`,network:'Não foi possível concluir. Verifique sua conexão e atualize a lista.',denied:'A Gestão está disponível apenas para Master e Admin.',count:n=>`${n} usuário(s) encontrado(s).`,themeLight:'Ativar tema claro',themeDark:'Ativar tema escuro'},
  en: { title:'Management',subtitle:'Link apartments to owners and manage accounts (Master/Admin)',pending:'Pending',approved:'Approved',rejected:'Rejected',all:'All',search:'Search by name or email...',allRoles:'All roles',noRole:'No role',name:'Name',email:'Email',status:'Status',role:'Role',actions:'Actions',empty:'No users',loading:'Loading users…',approve:'Approve',reject:'Reject',remove:'Delete account',cancel:'Cancel',confirm:'Confirm',saving:'Saving…',retry:'Try again',updated:'Last updated: ',readOnly:'Read-only access. Only Master can change accounts and permissions.',self:'You cannot change your own account.',multi:'Multiple roles; existing permissions preserved.',logout:'Sign out',missingName:'No name',success:'Changes saved.',deleted:'Account deleted.',deleteTitle:'Permanently delete account',deleteDescription:email=>`The Supabase account for ${email} will be deleted, including access to the current site. This cannot be undone.`,typeEmail:'Enter the account email to confirm',wrongEmail:'Enter the exact account email.',statusTitle:action=>action==='approved'?'Approve account':'Reject account',statusDescription:(name,status)=>status==='approved'?`Approve access for ${name}? The account status will also change on the current site.`:`Reject access for ${name}? The account will lose access to this portal and the current site.`,roleTitle:'Change role',roleDescription:(name,role)=>`Change ${name}'s role to ${role}? This also changes permissions on the current site.`,unavailable:name=>`${name} will be available in a later stage.`,network:'Unable to complete. Check your connection and refresh the list.',denied:'Management is available to Master and Admin only.',count:n=>`${n} user(s) found.`,themeLight:'Switch to light theme',themeDark:'Switch to dark theme'},
  es: { title:'Gestión',subtitle:'Vincula apartamentos a propietarios y gestiona cuentas (Master/Admin)',pending:'Pendientes',approved:'Aprobados',rejected:'Rechazados',all:'Todos',search:'Buscar por nombre o email...',allRoles:'Todos los roles',noRole:'Sin rol',name:'Nombre',email:'Email',status:'Estado',role:'Rol',actions:'Acciones',empty:'Ningún usuario',loading:'Cargando usuarios…',approve:'Aprobar',reject:'Rechazar',remove:'Eliminar cuenta',cancel:'Cancelar',confirm:'Confirmar',saving:'Guardando…',retry:'Intentar de nuevo',updated:'Última actualización: ',readOnly:'Acceso de consulta. Solo Master puede cambiar cuentas y permisos.',self:'No puedes modificar tu propia cuenta.',multi:'Múltiples roles; permisos existentes preservados.',logout:'Cerrar sesión',missingName:'Sin nombre',success:'Cambios guardados.',deleted:'Cuenta eliminada.',deleteTitle:'Eliminar cuenta permanentemente',deleteDescription:email=>`Se eliminará la cuenta Supabase de ${email}, incluido el acceso al sitio actual. Esta acción es irreversible.`,typeEmail:'Escribe el email de la cuenta para confirmar',wrongEmail:'Escribe exactamente el email de la cuenta.',statusTitle:action=>action==='approved'?'Aprobar cuenta':'Rechazar cuenta',statusDescription:(name,status)=>status==='approved'?`¿Aprobar el acceso de ${name}? También cambiará el estado de la cuenta en el sitio actual.`:`¿Rechazar el acceso de ${name}? La cuenta perderá el acceso a este portal y al sitio actual.`,roleTitle:'Cambiar rol',roleDescription:(name,role)=>`¿Cambiar el rol de ${name} a ${role}? También cambiará los permisos en el sitio actual.`,unavailable:name=>`${name} se implementará en una próxima etapa.`,network:'No se pudo completar. Comprueba tu conexión y actualiza la lista.',denied:'La Gestión está disponible solo para Master y Admin.',count:n=>`${n} usuario(s) encontrado(s).`,themeLight:'Activar tema claro',themeDark:'Activar tema oscuro'}
};

let language = readPreference('urban-language') || 'pt-BR';
if (!copy[language]) language = 'pt-BR';
let users = [], actor, currentStatus = 'pending', csrfToken, fetchedAt;
let loading = true, mutationBusy = false, pendingAction, toastTimeout;
let requestNumber = 0, writeController;
const t = () => copy[language];
function readPreference(key) { try { return localStorage.getItem(key); } catch { return null; } }
function savePreference(key,value) { try { localStorage.setItem(key,value); } catch { /* Preferences are optional. No auth tokens are stored here. */ } }

hydrateIcons();
applyTheme(readPreference('urban-theme') === 'light' ? 'light' : 'dark');
applyLanguage();

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function showError(message) { $('error-text').textContent = message; $('error-banner').hidden = !message; }
function toast(message) { clearTimeout(toastTimeout); $('toast').textContent = message; $('toast').hidden = false; toastTimeout = setTimeout(() => $('toast').hidden = true, 6000); }

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  $('brand-icon').src = `/assets/urban-infinity-icon-${theme}.png`;
  document.querySelector('link[rel=icon]').href = `/assets/urban-infinity-icon-${theme}.png`;
  $('theme-toggle').innerHTML = icon(theme === 'dark' ? 'sun' : 'moon');
  $('theme-toggle').setAttribute('aria-label', theme === 'dark' ? t().themeLight : t().themeDark);
  savePreference('urban-theme', theme);
}

function applyLanguage() {
  document.documentElement.lang = language;
  document.title = `${t().title} — Infinity Urban`;
  $('language').value = language;
  $('page-title').textContent = t().title;
  $('page-subtitle').textContent = t().subtitle;
  for (const status of ['pending','approved','rejected','all']) $(`tab-${status}`).textContent = t()[status];
  for (const name of ['name','email','status','role','actions']) $(`col-${name}`).textContent = t()[name];
  $('search').placeholder = t().search;
  $('role-filter').options[0].textContent = t().allRoles;
  $('role-filter').options[5].textContent = t().noRole;
  $('read-only').textContent = t().readOnly;
  $('retry').textContent = t().retry;
  $('cancel-dialog').textContent = t().cancel;
  $('confirm-email-label').textContent = t().typeEmail;
  $('logout').querySelector('span').textContent = t().logout;
  $('refresh').firstChild.textContent = t().updated;
  $('theme-toggle').setAttribute('aria-label', document.documentElement.dataset.theme === 'dark' ? t().themeLight : t().themeDark);
  updateTimestamp();
  render();
}

function updateActor() {
  if (!actor) return;
  installNavigation(actor);
  $('account-name').textContent = actor.fullName || actor.email;
  $('account-email').textContent = actor.email;
  $('account-role').textContent = actor.role || t().noRole;
  $('avatar').textContent = (actor.fullName || actor.email || 'U').trim().slice(0,1).toUpperCase();
  $('read-only').hidden = actor.canEdit;
}

function updateTimestamp() {
  if (fetchedAt) $('updated-at').textContent = new Intl.DateTimeFormat(language === 'pt-BR' ? 'en-US' : language, {year:'2-digit',month:'2-digit',day:'2-digit',hour:'numeric',minute:'2-digit',timeZone:'America/Sao_Paulo'}).format(new Date(fetchedAt));
}

function setStatus(status, focus = false) {
  currentStatus = status;
  for (const tab of $('status-tabs').querySelectorAll('[role=tab]')) {
    const selected = tab.dataset.status === status;
    tab.setAttribute('aria-selected', String(selected));
    tab.tabIndex = selected ? 0 : -1;
  }
  $('users-panel').setAttribute('aria-labelledby', `tab-${status}`);
  render();
  if (focus) $(`tab-${status}`).focus();
}

function render() {
  const body = $('users-body');
  body.replaceChildren();
  $('users-panel').setAttribute('aria-busy', String(loading));
  const search = $('search').value.trim().toLocaleLowerCase();
  const role = $('role-filter').value;
  const filtered = users.filter(user => (currentStatus === 'all' || user.status === currentStatus)
    && (!search || `${user.fullName || ''} ${user.email || ''}`.toLocaleLowerCase().includes(search))
    && (role === 'all' || (role === 'none' ? !user.roles.length : user.roles.some(row => row.role === role))));
  $('result-count').textContent = t().count(filtered.length);
  if (loading || !filtered.length) {
    const cell = element('td','empty',loading ? t().loading : t().empty);
    cell.colSpan = 5;
    const row = element('tr'); row.append(cell); body.append(row); return;
  }
  const fragment = document.createDocumentFragment();
  for (const user of filtered) {
    const self = actor?.id === user.id;
    const editable = actor?.canEdit && !self && !mutationBusy;
    const row = element('tr'); row.dataset.userId = user.id;
    row.append(element('td','member-name',user.fullName || t().missingName),element('td','member-email',user.email || '—'));
    const statusCell = element('td');
    // Keep the stored status labels, matching the original table.
    statusCell.append(element('span',`badge ${['pending','approved','rejected'].includes(user.status) ? user.status : ''}`,user.status));
    row.append(statusCell);
    const roleCell = element('td');
    if (user.roles.length > 1) {
      const text = element('span','multi-role',user.roles.map(item=>item.role).join(', ')); text.title = t().multi; roleCell.append(text);
    } else {
      const select = element('select','role-select');
      select.setAttribute('aria-label',`${t().role}: ${user.fullName || user.email}`);
      select.append(new Option(t().noRole,''));
      select.options[0].disabled = true;
      for (const role of ['master','admin','proprietario','funcionario']) select.append(new Option(role,role));
      select.value = user.role || '';
      select.disabled = !editable;
      if (self) select.title = t().self;
      select.addEventListener('change',()=>{const next = select.value;select.value = user.role || '';openAction({type:'role',user,role:next});});
      roleCell.append(select);
    }
    row.append(roleCell);
    const actionsCell = element('td'); const actions = element('div','row-actions');
    if (!self) {
      const status = user.status === 'approved' ? 'rejected' : 'approved';
      const button = element('button','outline-button',status === 'approved' ? t().approve : t().reject);
      button.disabled = !editable;
      button.addEventListener('click',()=>openAction({type:'status',user,status})); actions.append(button);
      if (user.status === 'pending') {
        const reject = element('button','outline-button',t().reject); reject.disabled = !editable;
        reject.addEventListener('click',()=>openAction({type:'status',user,status:'rejected'})); actions.append(reject);
      }
      const remove = element('button','danger-button',t().remove); remove.disabled = !editable || !user.email;
      remove.addEventListener('click',()=>openAction({type:'delete',user})); actions.append(remove);
    }
    actionsCell.append(actions);row.append(actionsCell);fragment.append(row);
  }
  body.append(fragment);
}

async function get(path) {
  const response = await fetch(path,{cache:'no-store',credentials:'same-origin',signal:AbortSignal.timeout(25000)});
  if (response.status === 401) { location.replace('/login'); throw new Error('Sua sessão expirou.'); }
  const data = await response.json().catch(()=>({}));
  if (!response.ok) throw new Error(data.error || (response.status === 403 ? t().denied : t().network));
  return data;
}

async function refresh({keepError=false}={}) {
  const request = ++requestNumber;
  loading = true;render();
  if (!keepError) showError('');
  try {
    const data = await get('/api/management/users');
    if (request !== requestNumber) return;
    users = data.users; actor = data.currentUser; fetchedAt = data.fetchedAt;
    updateActor();
    updateTimestamp();
  } catch (error) {
    if (request === requestNumber) { users = [];showError(error.name === 'TimeoutError' || error instanceof TypeError ? t().network : error.message); }
  } finally { if (request === requestNumber) {loading=false;render();} }
}

function openAction(action) {
  if (mutationBusy || !actor?.canEdit || action.user.id === actor.id) return;
  pendingAction = action;
  const name = action.user.fullName || action.user.email || t().missingName;
  const deletion = action.type === 'delete';
  $('dialog-title').textContent = deletion ? t().deleteTitle : action.type === 'role' ? t().roleTitle : t().statusTitle(action.status);
  $('dialog-description').textContent = deletion ? t().deleteDescription(action.user.email) : action.type === 'role' ? t().roleDescription(name,action.role) : t().statusDescription(name,action.status);
  $('confirm-email-label').hidden = !deletion; $('confirm-email').hidden = !deletion;
  $('confirm-email').required = deletion;$('confirm-email').value = '';
  $('dialog-error').hidden = true;
  $('confirm-action').className = deletion || action.status === 'rejected' ? 'danger-button' : 'primary-button';
  $('confirm-action').textContent = deletion ? t().remove : t().confirm;
  $('confirm-dialog').showModal();
  if (deletion) $('confirm-email').focus(); else $('cancel-dialog').focus();
}

async function post(path,body,signal) {
  if (!csrfToken) csrfToken = (await get('/api/auth/form')).csrfToken;
  signal?.throwIfAborted();
  const response = await fetch(path,{method:'POST',credentials:'same-origin',cache:'no-store',headers:{'Content-Type':'application/json','X-CSRF-Token':csrfToken},body:JSON.stringify(body),signal:signal?AbortSignal.any([signal,AbortSignal.timeout(25000)]):AbortSignal.timeout(25000)});
  if (response.status === 401) {location.replace('/login');throw new Error('Sua sessão expirou.');}
  const data = await response.json().catch(()=>({}));
  if (!response.ok) {if(response.status===400)csrfToken=undefined;throw new Error(data.error || t().network);}
  return data;
}

$('confirm-form').addEventListener('submit',async event=>{
  event.preventDefault();
  if (mutationBusy || !pendingAction) return;
  const action = pendingAction;
  if (action.type === 'delete' && $('confirm-email').value.trim() !== action.user.email) {
    $('dialog-error').textContent = t().wrongEmail;$('dialog-error').hidden=false;return;
  }
  mutationBusy=true;writeController=new AbortController();
  $('confirm-action').disabled=true;$('cancel-dialog').disabled=true;$('close-dialog').disabled=true;
  $('confirm-action').textContent=t().saving;
  render();
  let errorMessage;
  try {
    const path = `/api/management/users/${encodeURIComponent(action.user.id)}/${action.type}`;
    const body = action.type === 'delete' ? {confirmEmail:action.user.email} : action.type === 'role'
      ? {role:action.role,expectedRoleId:action.user.roles[0]?.id || null,expectedRole:action.user.roles[0]?.role || null}
      : {status:action.status,expectedStatus:action.user.status};
    await post(path,body,writeController.signal);toast(action.type==='delete'?t().deleted:t().success);
  } catch(error) { errorMessage = error.name==='TimeoutError'||error instanceof TypeError?t().network:error.message; }
  finally {
    mutationBusy=false;pendingAction=undefined;writeController=undefined;
    $('confirm-action').disabled=false;$('cancel-dialog').disabled=false;$('close-dialog').disabled=false;
    $('confirm-dialog').close();
    // Always reload after a write, including an uncertain network response.
    if(errorMessage)showError(errorMessage);
    await refresh({keepError:!!errorMessage});
    $('search').focus();
  }
});

for(const id of ['close-dialog','cancel-dialog'])$(id).addEventListener('click',()=>{if(!mutationBusy){$('confirm-dialog').close();pendingAction=undefined;}});
$('confirm-dialog').addEventListener('cancel',event=>{if(mutationBusy)event.preventDefault();else pendingAction=undefined;});
$('search').addEventListener('input',render);$('role-filter').addEventListener('change',render);
for(const tab of $('status-tabs').querySelectorAll('[role=tab]')){
  tab.addEventListener('click',()=>setStatus(tab.dataset.status));
  tab.addEventListener('keydown',event=>{
    const statuses=['pending','approved','rejected','all'];let index=statuses.indexOf(currentStatus);
    if(event.key==='ArrowRight')index=(index+1)%4;else if(event.key==='ArrowLeft')index=(index+3)%4;else if(event.key==='Home')index=0;else if(event.key==='End')index=3;else return;
    event.preventDefault();setStatus(statuses[index],true);
  });
}
$('refresh').addEventListener('click',()=>{if(!mutationBusy)refresh();});$('retry').addEventListener('click',()=>refresh());
$('language').addEventListener('change',()=>{language=$('language').value;savePreference('urban-language',language);applyLanguage();});
$('theme-toggle').addEventListener('click',()=>applyTheme(document.documentElement.dataset.theme==='dark'?'light':'dark'));
$('account-toggle').addEventListener('click',()=>{const open=$('account-popover').hidden;$('account-popover').hidden=!open;$('account-toggle').setAttribute('aria-expanded',String(open));});
$('notifications-toggle').addEventListener('click',()=>{const open=$('notifications-popover').hidden;$('notifications-popover').hidden=!open;$('notifications-toggle').setAttribute('aria-expanded',String(open));});
document.addEventListener('click',event=>{
  if(!event.target.closest('.account-menu')){$('account-popover').hidden=true;$('account-toggle').setAttribute('aria-expanded','false');}
  if(!event.target.closest('#notifications-toggle')&&!event.target.closest('#notifications-popover')){$('notifications-popover').hidden=true;$('notifications-toggle').setAttribute('aria-expanded','false');}
});
document.addEventListener('keydown',event=>{if(event.key==='Escape'){$('account-popover').hidden=true;$('notifications-popover').hidden=true;closeSidebar();}});
$('logout').addEventListener('click',async()=>{
  $('logout').disabled=true;
  try{await post('/api/auth/logout',{});location.replace('/login');}catch(error){showError(error.message);$('logout').disabled=false;}
});
function closeSidebar(){const mobile=matchMedia('(max-width:760px)').matches;document.body.classList.remove('sidebar-open');$('sidebar-backdrop').hidden=true;$('sidebar').inert=mobile;$('toggle-sidebar').setAttribute('aria-expanded',String(!mobile&&!document.body.classList.contains('sidebar-collapsed')));$('toggle-sidebar').setAttribute('aria-label',mobile?'Abrir navegação':'Recolher navegação');}
$('toggle-sidebar').addEventListener('click',()=>{
  if(matchMedia('(max-width:760px)').matches){const open=document.body.classList.toggle('sidebar-open');$('sidebar-backdrop').hidden=!open;$('sidebar').inert=!open;$('toggle-sidebar').setAttribute('aria-expanded',String(open));$('toggle-sidebar').setAttribute('aria-label',open?'Fechar navegação':'Abrir navegação');}
  else{const collapsed=document.body.classList.toggle('sidebar-collapsed');$('toggle-sidebar').setAttribute('aria-expanded',String(!collapsed));}
});
$('sidebar-backdrop').addEventListener('click',closeSidebar);
matchMedia('(max-width:760px)').addEventListener('change',closeSidebar);closeSidebar();
for(const button of document.querySelectorAll('[data-section]')){button.title=button.dataset.section;button.addEventListener('click',()=>{toast(t().unavailable(button.dataset.section));closeSidebar();});}
if($('assistant'))$('assistant').addEventListener('click',()=>toast(t().unavailable('Assistente IA')));
function resetDialog() {
  $('confirm-dialog').close();pendingAction=undefined;mutationBusy=false;
  for(const id of ['confirm-action','cancel-dialog','close-dialog'])$(id).disabled=false;
}
window.addEventListener('pagehide',()=>{writeController?.abort();++requestNumber;resetDialog();});
window.addEventListener('pageshow',async event=>{if(event.persisted){writeController?.abort();resetDialog();await initialize();}});
async function initialize() {
  loading=true;render();
  try {
    const session=await get('/api/auth/session');
    actor=session.member;
    if(!actor?.canManage)throw new Error(t().denied);
    updateActor();
  } catch(error) {
    loading=false;users=[];showError(error.name==='TimeoutError'||error instanceof TypeError?t().network:error.message);render();return;
  }
  await refresh();
}
await initialize();
