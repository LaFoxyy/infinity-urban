import fs from'node:fs';import vm from'node:vm';import assert from'node:assert/strict';import{webcrypto}from'node:crypto';
class Element{
 constructor(value=''){this.value=value;this.children=[];this.events={};this.dataset={};this.hidden=false;this.style={};this.fields=new Map();}
 get options(){return this.children;}
 addEventListener(k,f){this.events[k]=f;}replaceChildren(...a){this.children=a;}append(...a){this.children.push(...a);}setAttribute(k,v){this[k]=v;}showModal(){this.open=true;}close(){this.open=false;}reset(){for(const field of this.fields.values())field.value='';}remove(){}
 querySelector(selector){if(!this.fields.has(selector))this.fields.set(selector,new Element());return this.fields.get(selector);}
 querySelectorAll(selector){return selector==='[name=facilities]:checked'?ids.get('building-facility-options').children.flatMap(label=>label.children).filter(c=>c.name==='facilities'&&c.checked):[];}
}
const ids=new Map();const $=id=>{if(!ids.has(id))ids.set(id,new Element());return ids.get(id);};
const input={title:'Prédio atual',currency:'BRL',timezone:'America/Sao_Paulo',address:'Rua atual',city:'São Paulo',state:'SP',country:'BR',zipCode:'01234-000',email:'a@example.com',phone:'123',description:'Descrição atual',importantInformation:'Orientações atuais',facilities:['f1']};
const catalog={environment:'production',properties:[{id:'p1',title:input.title,currency:'BRL',photos:[]}],rooms:[]};let writes=[],failRead=false;
const context={document:{getElementById:$,querySelector:()=>new Element(),querySelectorAll:()=>[],createTextNode:text=>({textContent:text})},Option:class extends Element{constructor(text,value){super(value);this.textContent=text;}},FormData:class{constructor(form){this.form=form;}entries(){return [...this.form.fields.entries()].filter(([s,f])=>s.startsWith('[name=')&&!f.disabled).map(([s,f])=>[s.match(/"(.*?)"/)[1],f.value])[Symbol.iterator]();}},URLSearchParams,URL:{revokeObjectURL(){}},location:{search:''},crypto:webcrypto,console,Intl,icon:()=>'',node:(tag,cls,text)=>Object.assign(new Element(),{tag,className:cls,textContent:text}),initShell:async()=>({canManage:true,isRestrictedOwner:false}),updateTimestamp(){},ownership:async()=>({owners:[],assignments:[]}),editOwner:async()=>null,assignRoomOwner:async()=>false,uploadPhoto:async()=>{},requestJson:async(path,body)=>{
 if(body){writes.push({path,body});return{confirmed:true};}
 if(path==='/api/accommodations')return catalog;
 if(path==='/api/accommodations/property-facilities')return[{id:'f1',attributes:{title:'Elevador'}}];
 if(path.endsWith('/edit')){if(failRead)throw Error('Falha simulada');return{id:'p1',revision:'current-revision',input,photos:[{url:'https://img.channex.io/one',ticket:'existing-one'},{url:'https://img.channex.io/two',ticket:'existing-two'}],logo:{url:'https://img.channex.io/logo',ticket:'existing-logo'}};}
 throw Error(path);
}};
let source=fs.readFileSync('src/InfinityUrban.Web/wwwroot/unified-accommodations.js','utf8').replace(/^import.+\r?\n/gm,'');
source+='\nglobalThis.editorHooks={openBuilding};';await vm.runInNewContext('(async()=>{'+source+'})()',context);
$('property-grid').children[0].children[0].events.click();assert.equal($('edit-building').hidden,false);await $('edit-building').events.click();
assert.equal($('building-form-title').textContent,'Editar prédio');assert.equal($('building-upload-list').children.length,2);assert.equal($('building-logo-preview').children.length,1);
const form=$('building-form');assert.equal(form.querySelector('[name="title"]').value,input.title);assert.equal(form.querySelector('[name="currency"]').disabled,true);assert.equal(form.querySelector('[name="address"]').required,false);
// Reorder and remove existing photos through the same visible controls used in the page.
await $('building-upload-list').children[0].children[2].children[1].events.click();
await $('building-upload-list').children[1].children[2].children[2].events.click();
form.querySelector('[name="title"]').value='Prédio novo';await form.events.submit({preventDefault(){},currentTarget:form});
assert.equal(writes[0].path,'/api/accommodations/properties/p1/edit');assert.equal(writes[0].body.input.title,'Prédio novo');assert.equal(writes[0].body.input.currency,'BRL');assert.deepEqual(Array.from(writes[0].body.input.photoTickets),['existing-two']);assert.equal(writes[0].body.input.logoTicket,'existing-logo');assert.equal(writes[0].body.expectedRevision,'current-revision');assert.equal($('building-dialog').open,false);
failRead=true;await context.editorHooks.openBuilding(catalog.properties[0]);assert.equal(form.querySelector('[type=submit]').disabled,true);await form.events.submit({preventDefault(){},currentTarget:form});assert.equal(writes.length,1);
failRead=false;$('building-dialog').close();await $('add-building').events.click();assert.equal($('building-form-title').textContent,'Adicionar prédio');assert.equal(form.querySelector('[name="currency"]').disabled,false);assert.equal(form.querySelector('[name="address"]').required,true);assert.equal(form.querySelector('[type=submit]').disabled,false);assert.equal($('building-upload-list').children.length,0);
console.log('PASS Building editor preloads text, facilities, photos and logo; edit saves IDs and revision; failed load blocks save; new creation resets edit state.');
