import fs from 'node:fs';
const text=fs.readFileSync('src/InfinityUrban.Web/wwwroot/app.js','utf8');
const start=text.indexOf('async function showSession()');
const body=text.slice(start,text.indexOf("for (const value of ['login', 'signup'])",start));
let destination;
globalThis.location={replace:path=>{destination=path;}};
for(const member of [{isRestrictedOwner:true,isOperator:false},{isRestrictedOwner:false,isOperator:true}]){
 globalThis.fetch=async()=>({status:200,ok:true,json:async()=>({member})});
 await eval(body+'showSession()');
 if(destination!=='/acomodacoes')throw new Error('Login does not open the correct portfolio');
 destination=undefined;
}
console.log('PASS Owner and team login open the authorized accommodation portfolio');
