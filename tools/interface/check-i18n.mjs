import assert from 'node:assert/strict';
import {translate,amenityLabel,localize,setLanguage} from '../../src/InfinityUrban.Web/wwwroot/i18n.js';
import {amenities} from '../../src/InfinityUrban.Web/wwwroot/amenities.js';
import {parseAmount,amountInput} from '../../src/InfinityUrban.Web/wwwroot/expense-money.js';

assert.equal(translate('Acomodações','en'),'Accommodations');
assert.equal(translate('Accommodations','pt-BR'),'Acomodações');
assert.equal(translate('+ Adicionar apartamento','en'),'+ Add apartment');
assert.equal(translate('Relatórios — Infinity Urban','en'),'Reports — Infinity Urban');
assert.equal(translate('3 apartamento(s)','en'),'3 apartment(s)');
assert.equal(translate('3 de 12','en'),'3 of 12');
assert.equal(translate('11/1/26 a 11/30/26','en'),'11/1/26 to 11/30/26');
assert.equal(translate('funcionario','en'),'Employee');
assert.equal(translate('pending','pt-BR'),'Pendente');
assert.equal(translate('Solicitação recebida. Se necessário, confirme seu email. O acesso depende da aprovação de um administrador.','en'),'Request received. Confirm your email if required. Access is subject to administrator approval.');
assert.equal(Object.keys(amenities).length,464);
for(const [en,pt]of Object.entries(amenities)){assert.ok(pt.trim());assert.equal(amenityLabel(en,'pt-BR'),pt);assert.equal(amenityLabel(en,'en'),en);}
assert.equal(amenityLabel('Airconditioning','pt-BR'),'Ar-condicionado');
assert.equal(amenityLabel('Accessible by Lift','pt-BR'),'Acesso por elevador');
assert.equal(amenityLabel('Unknown future amenity','pt-BR'),'Unknown future amenity');
class Element{
  constructor(tag='div'){this.tag=tag;this.nodeType=1;this.children=[];this.dataset={};this.attrs={};}
  append(...nodes){for(const n of nodes){n.parentElement=this;this.children.push(n);}}
  closest(selector){if(selector==='[data-amenity-title]')return this.dataset.amenityTitle?this:this.parentElement?.closest(selector);if(selector==='option')return this.tag==='option'?this:this.parentElement?.closest(selector);if(selector.includes('[data-i18n-skip]')&&this.dataset.i18nSkip)return this;return this.parentElement?.closest(selector);}
  hasAttribute(k){return k in this.attrs;}getAttribute(k){return this.attrs[k];}setAttribute(k,v){this.attrs[k]=v;}
}
const html=new Element(),label=new Element('label'),checkbox=new Element('input'),caption={nodeType:3,nodeValue:'Airconditioning'},ordinary={nodeType:3,nodeValue:'Carregando…'},literal=new Element();
label.dataset.amenityTitle='Airconditioning';checkbox.value='c88b8629-9b10-44d4-a237-f5d37eeeddf4';checkbox.checked=true;label.append(checkbox,caption);
const userText={nodeType:3,nodeValue:'Acomodações'};literal.dataset.i18nSkip='true';literal.append(userText);
const search=new Element('input');search.attrs.placeholder='Buscar apartamento…';html.append(label,ordinary,literal,search);
globalThis.document={documentElement:html,cookie:'',getElementById:()=>null,createTreeWalker(root){const all=[];const visit=node=>{for(const child of node.children||[]){all.push(child);visit(child);}};visit(root);return{nextNode:()=>all.shift()};}};
globalThis.NodeFilter={SHOW_ELEMENT:1,SHOW_TEXT:4};globalThis.window={dispatchEvent(){}};
globalThis.location={protocol:'https:'};const preferences=new Map();globalThis.localStorage={getItem:key=>preferences.get(key),setItem:(key,value)=>preferences.set(key,value)};
setLanguage('pt-BR');assert.equal(caption.nodeValue,'Ar-condicionado');
setLanguage('en');assert.equal(caption.nodeValue,'Airconditioning');assert.equal(ordinary.nodeValue,'Loading…');assert.equal(search.attrs.placeholder,'Search apartments…');
assert.equal(checkbox.checked,true);assert.equal(checkbox.value,'c88b8629-9b10-44d4-a237-f5d37eeeddf4');assert.equal(userText.nodeValue,'Acomodações');
ordinary.nodeValue='Nenhum apartamento encontrado.';localize(ordinary);assert.equal(ordinary.nodeValue,'No apartments found.');
assert.equal(parseAmount('1,234.56'),123456);assert.equal(parseAmount('1.234,56'),123456);assert.equal(parseAmount(amountInput(123456)),123456);
setLanguage('pt-BR');assert.equal(caption.nodeValue,'Ar-condicionado');assert.equal(search.attrs.placeholder,'Buscar apartamento…');assert.equal(ordinary.nodeValue,'Nenhum apartamento encontrado.');
assert.equal(parseAmount('1.234,56'),123456);assert.equal(parseAmount(amountInput(123456)),123456);
console.log('PASS Portuguese/English labels, dynamic messages, all 464 amenities, language switching, literal user data, checkbox UUID/selection preservation and exact localized money.');
