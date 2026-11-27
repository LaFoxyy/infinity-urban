import {english} from './translations.js';
import {amenities} from './amenities.js';

let language='pt-BR';
try{language=localStorage.getItem('urban-language')?.startsWith('en')?'en':'pt-BR';}catch{}
export const getLanguage=()=>language;
export const getLocale=()=>language==='en'?'en-US':'pt-BR';
const portuguese=new Map();for(const [pt,en]of Object.entries(english))if(!portuguese.has(en))portuguese.set(en,pt);
export function amenityLabel(title,lang=language){return lang==='en'?title:amenities[title]||title;}
const rules=[
  [/^(\d{1,2}\/\d{1,2}\/\d{2,4}) a (\d{1,2}\/\d{1,2}\/\d{2,4})$/,(_,from,to)=>`${from} to ${to}`],
  [/^(\d+) (prédio\(s\)|apartamento\(s\)|anúncio\(s\)|noite\(s\))$/,(_,n,unit)=>`${n} ${{'prédio(s)':'building(s)','apartamento(s)':'apartment(s)','anúncio(s)':'listing(s)','noite(s)':'night(s)'}[unit]}`],
  [/^(\d+) de (\d+)( anúncio\(s\))?$/,(_,n,total,listings)=>`${n} of ${total}${listings?' listing(s)':''}`],
  [/^(\d+) apartamento\(s\) selecionado\(s\)$/,(_,n)=>`${n} apartment(s) selected`],
  [/^(\d+) estadia\(s\) sem valor confirmado$/,(_,n)=>`${n} stay(s) without a confirmed amount`],
  [/^(\d+) estadia\(s\) sem valor · parcial$/,(_,n)=>`${n} stay(s) without an amount · partial`],
  [/^(\d+) cancelada\(s\) fora do faturamento$/,(_,n)=>`${n} cancelled reservation(s) excluded from revenue`],
  [/^([\d.,]+) de ([\d.,]+) noites$/,(_,n,total)=>`${n} of ${total} nights`],
  [/^([\d.,—]+) (hóspedes|quartos|banheiros)$/,(_,n,unit)=>`${n} ${{hóspedes:'guests',quartos:'bedrooms',banheiros:'bathrooms'}[unit]}`],
  [/^Página (\d+) de (\d+) · (\d+) reserva\(s\)$/,(_,page,total,n)=>`Page ${page} of ${total} · ${n} reservation(s)`],
  [/^(\d+) apartamento\(s\) · Noites disponíveis = dias do período × quantidade de unidades$/,(_,n)=>`${n} apartment(s) · Available nights = days in period × number of units`],
  [/^(\d+) proprietários? cadastrados?(?: · (\d+) encontrado\(s\))?$/,(_,n,found)=>`${n} registered owner(s)${found?` · ${found} found`:''}`],
  [/^(\d+) unidade\(s\) disponível\(is\) na Channex$/,(_,n)=>`${n} unit(s) available in Channex`],
  [/^(\d+) recebimento\(s\) de reserva aguardando confirmação\.$/,(_,n)=>`${n} incoming reservation(s) awaiting confirmation.`],
  [/^(\d+) alteração\(ões\) de disponibilidade na fila de envio\.$/,(_,n)=>`${n} availability change(s) queued for delivery.`],
  [/^Ações de (.+)$/,(_,name)=>`Actions for ${name}`],
  [/^Logo de (.+)$/,(_,name)=>`Logo for ${name}`],
  [/^Desvincular este apartamento de (.+)\?$/,(_,name)=>`Unlink this apartment from ${name}?`],
  [/^Excluir o cadastro de (.+)\?$/,(_,name)=>`Delete the record for ${name}?`],
  [/^Remover o acesso de (.+) a (.+)\?$/,(_,name,room)=>`Remove ${name}'s access to ${room}?`],
  [/^O limite desta galeria é de (\d+) fotos\.$/,(_,n)=>`This gallery is limited to ${n} photos.`],
  [/^Não foi possível concluir a operação \((\d+)\)\.$/,(_,n)=>`Unable to complete the operation (${n}).`],
  [/^A exclusão de “(.+)” na Channex é permanente\. Digite o nome abaixo para confirmar\. Cadastros com reservas ou vínculos ativos podem ter a exclusão bloqueada\.$/,(_,name)=>`Deleting “${name}” in Channex is permanent. Enter the name below to confirm. Records with reservations or active links may be protected from deletion.`],
  [/^(Código|Moeda|Fuso|Email|Telefone|CEP|Origem): (.*)$/,(_,label,value)=>`${{Código:'Code',Moeda:'Currency',Fuso:'Time zone',Email:'Email',Telefone:'Phone',CEP:'Postal code',Origem:'Source'}[label]}: ${value}`],
  [/^Use a moeda da acomodação \((.+)\) para esta despesa\.$/,(_,currency)=>`Use the accommodation currency (${currency}) for this expense.`],
  [/^A Channex não confirmou disponibilidade em (.+)\.$/,(_,date)=>`Channex did not confirm availability on ${date}.`],
  [/^(.+) Confira a lista antes de iniciar outro cadastro\.$/,(_,message)=>`${translate(message)} Check the list before starting another record.`],
  [/^(.+) Confira a lista de cadastros antes de iniciar outra tentativa\.$/,(_,message)=>`${translate(message)} Check existing records before trying again.`],
  [/^O apartamento já foi criado na Channex\. O vínculo no Supabase ainda não foi concluído: (.+) Tente concluir o vínculo; não crie outro apartamento\.$/,(_,message)=>`The apartment was already created in Channex. The Supabase link is incomplete: ${translate(message)} Complete the link; do not create another apartment.`],
];
export function translate(value,lang=language){
  if(typeof value!=='string'||!value.trim())return value;
  const core=value.trim(),prefix=value.slice(0,value.indexOf(core)),suffix=value.slice(value.indexOf(core)+core.length);
  const roles={master:['Master','Master'],admin:['Admin','Admin'],funcionario:['Funcionário','Employee'],proprietario:['Proprietário','Owner'],pending:['Pendente','Pending'],approved:['Aprovado','Approved'],rejected:['Rejeitado','Rejected']};
  let result=roles[core]?.[lang==='en'?1:0]??(lang==='en'?english[core]:portuguese.get(core));
  if(result===undefined&&lang==='en'){
    for(const [pattern,render]of rules){const match=core.match(pattern);if(match){result=render(...match);break;}}
    if(result===undefined&&/^\+ |^← /.test(core))result=core.slice(0,2)+translate(core.slice(2),lang);
    if(result===undefined&&/ [↕↓↑]$/.test(core))result=translate(core.slice(0,-2),lang)+core.slice(-2);
    if(result===undefined&&core.endsWith(' — Infinity Urban'))result=translate(core.slice(0,-17),lang)+' — Infinity Urban';
    if(result===undefined&&core.includes(' · '))result=core.split(' · ').map(part=>translate(part,lang)).join(' · ');
  }
  return prefix+(result??core)+suffix;
}

const originals=new WeakMap(),attributes=new WeakMap();let initialized=false,observer;
const protectedSelector='script,style,textarea,code,pre,[translate="no"],[data-i18n-skip],#account-name,#account-email,#session-email,.detail-description,.important-information,.owner-contact,.member-name,.member-email,.timeline-booking,#property-grid h2,#property-grid h3,.owner-room-text,.timeline-room-label strong';
function text(node){
  if(node.parentElement?.closest(protectedSelector))return;
  const option=node.parentElement?.closest('option');
  if(option&&/^(?:room:|property:)?[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(option.value))return;
  const current=node.nodeValue,record=originals.get(node);
  const source=record&&current===record.rendered?record.source:current;
  const amenity=node.parentElement?.closest('[data-amenity-title]');
  const rendered=amenity?amenityLabel(amenity.dataset.amenityTitle):translate(source);
  originals.set(node,{source,rendered});if(current!==rendered)node.nodeValue=rendered;
}
function element(node){
  if(node.closest?.(protectedSelector))return;
  const saved=attributes.get(node)||{};
  for(const key of ['title','placeholder','aria-label','alt'])if(node.hasAttribute(key)){
    const current=node.getAttribute(key),prior=saved[key],source=prior&&current===prior.rendered?prior.source:current,rendered=translate(source);
    saved[key]={source,rendered};if(current!==rendered)node.setAttribute(key,rendered);
  }
  attributes.set(node,saved);
}
export function localize(root=document.documentElement){
  if(root.nodeType===3){text(root);return;}
  if(root.nodeType===1)element(root);
  const walker=document.createTreeWalker(root,NodeFilter.SHOW_ELEMENT|NodeFilter.SHOW_TEXT);
  let node;while(node=walker.nextNode())if(node.nodeType===3)text(node);else element(node);
}
export function setLanguage(next){
  language=next?.startsWith('en')?'en':'pt-BR';
  try{localStorage.setItem('urban-language',language);}catch{}
  document.documentElement.lang=language==='en'?'en':'pt-BR';
  document.cookie=`urban-language=${language}; Path=/; SameSite=Lax${location.protocol==='https:'?'; Secure':''}`;
  const select=document.getElementById('language');if(select)select.value=language;
  localize();window.dispatchEvent(new CustomEvent('urban-language-change',{detail:{language}}));
}
export function initLanguage(){
  const select=document.getElementById('language');
  if(select){select.replaceChildren(new Option('Português','pt-BR'),new Option('English','en'));select.value=language;if(!select.dataset.i18nBound){select.dataset.i18nBound='true';select.addEventListener('change',()=>setLanguage(select.value));}}
  if(initialized){localize();return;}
  initialized=true;document.documentElement.lang=language==='en'?'en':'pt-BR';
  document.cookie=`urban-language=${language}; Path=/; SameSite=Lax${location.protocol==='https:'?'; Secure':''}`;
  const confirm=window.confirm?.bind(window);if(confirm)window.confirm=message=>confirm(translate(message));
  document.addEventListener('invalid',event=>{const input=event.target;if(!input.setCustomValidity)return;input.setCustomValidity('');if(language==='en'&&!input.validity.valid){const message=input.validity.valueMissing?'Please fill out this field.':input.validity.typeMismatch?'Please enter a valid value.':input.validity.tooShort?`Use at least ${input.minLength} characters.`:'Please check this value.';input.setCustomValidity(message);}},true);
  document.addEventListener('input',event=>event.target.setCustomValidity?.(''));
  localize();observer=new MutationObserver(changes=>{const roots=new Set();for(const change of changes){if(change.type==='childList')for(const node of change.addedNodes)roots.add(node);else roots.add(change.target);}for(const root of roots)if(root.isConnected)localize(root);});
  observer.observe(document.documentElement,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:['title','placeholder','aria-label','alt']});
}
