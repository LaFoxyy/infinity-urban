import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

class Element {
  constructor(value=''){this.value=value;this.children=[];this.events={};this.dataset={};this.hidden=false;this.textContent='';}
  append(...children){this.children.push(...children);}
  replaceChildren(...children){this.children=children;}
  setAttribute(key,value){this[key]=value;}
  addEventListener(key,action){this.events[key]=action;}
}
const apartment={id:'room-1',title:'Apartamento <A>',building:'Prédio 1',reservations:1,occupiedNights:2,totalNights:30,occupancy:6.7,revenueCents:30301,missingAmounts:0};
const fixture={from:'2028-04-01',to:'2028-04-30',currency:'BRL',rooms:[apartment],apartments:[apartment],best:[apartment],worst:[apartment],daily:[{date:'2028-04-01',revenueCents:15000,occupiedNights:1},{date:'2028-04-02',revenueCents:15301,occupiedNights:1}],statuses:{Confirmadas:1},totals:{revenueCents:30301,reservations:1,occupiedNights:2,totalNights:30,occupancy:6.7,averageDailyCents:15151,missingAmounts:0,cancelled:0,expenseCents:5000,balanceCents:25301},expenses:[{id:'e1',name:'Limpeza',kind:'per_stay',category:'Limpeza',target:'Apartamento <A>',amountCents:5000}],restricted:false,canSeeExpenses:true,fetchedAt:'2028-04-01T12:00:00Z',notices:['Receita confirmada']};
async function run(isReport,isOwner){
  const ids=new Map(),get=id=>{if(!ids.has(id))ids.set(id,new Element());return ids.get(id);};
  let fail=false,printed=0,exported='';const requests=[];
  const context={document:{getElementById:get,querySelectorAll:()=>[],createElementNS:(_,tag)=>Object.assign(new Element(),{tag})},Option:class extends Element{constructor(label,value){super(value);this.textContent=label;}},URLSearchParams,Intl,Date,console,
    location:{pathname:isReport?'/relatorios':'/dashboard',search:'?from=2028-04-01&to=2028-04-30&roomId=room-1&currency=BRL',assign:url=>{exported=url;}},history:{replaceState(){}},window:{print(){printed++;}},
    node:(tag,className,text)=>Object.assign(new Element(),{tag,className,textContent:text}),updateTimestamp(){},initShell:async()=>({}),requestJson:async path=>{requests.push(path);if(fail)throw new Error('Designações indisponíveis');return {...fixture,restricted:isOwner,canSeeExpenses:!isOwner,expenses:isOwner?[]:fixture.expenses};}};
  const source=fs.readFileSync('src/InfinityUrban.Web/wwwroot/analytics.js','utf8').replace(/^import .+\r?\n/gm,'');
  await vm.runInNewContext('(async()=>{'+source+'})()',context);
  assert.equal(get('analytics-content').hidden,false);
  assert.equal(get('analytics-title').textContent,isReport?'Relatórios':'Dashboard');
  assert.equal(get('analytics-charts').hidden,isReport);
  assert.equal(get('report-expenses').hidden,isOwner);
  assert.equal(get('performance-rows').children.length,1);
  assert.equal(get('best-rows').children.length,1);
  assert.equal(get('revenue-chart').children[0].tag,'svg');
  assert.equal(get('daily-rows').children.length,2);
  assert.equal(get('analytics-room').value,'room-1');
  get('export-csv').events.click();assert.match(exported,/\/api\/analytics\/export.csv\?.*roomId=room-1/);
  get('print-report').events.click();assert.equal(printed,1);
  fail=true;await get('refresh').events.click();assert.equal(get('analytics-content').hidden,true);assert.equal(get('export-csv').disabled,true);assert.equal(get('analytics-error').hidden,false);
}
await run(false,false);await run(true,false);await run(true,true);
console.log('PASS Analytics filters, scoped CSV, PDF print, charts, expense visibility and clearing stale financial data on access failure (simulated DOM).');
