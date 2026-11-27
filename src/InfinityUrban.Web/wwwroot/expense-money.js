const locale=()=>typeof document!=='undefined'&&document.documentElement?.lang==='en'?'en-US':'pt-BR';
export function parseAmount(value){
  let text=String(value).trim().replace(/^R\$\s*/,'').replace(/\s/g,'');
  // Accept unambiguous grouped English input while retaining previously entered Portuguese values.
  if(locale()==='en-US'&&/^\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?$/.test(text))text=text.replace(/,/g,'');
  let whole,fraction='';
  if(text.includes(',')){if(!/^(?:\d+|\d{1,3}(?:\.\d{3})+),\d{1,2}$/.test(text))throw new Error('Informe um valor como 180,00 ou 1.234,56.');[whole,fraction]=text.split(',');whole=whole.replace(/\./g,'');}
  else if(locale()==='pt-BR'&&/^\d{1,3}(?:\.\d{3})+$/.test(text)){whole=text.replace(/\./g,'');}
  else{if(!/^\d+(?:\.\d{1,2})?$/.test(text))throw new Error('Informe um valor válido com até duas casas decimais.');[whole,fraction='']=text.split('.');}
  const cents=Number(whole)*100+Number(fraction.padEnd(2,'0'));if(!Number.isSafeInteger(cents)||cents<1||cents>99999999999)throw new Error('O valor deve ser positivo e estar dentro do limite permitido.');return cents;
}
export const formatAmount=(cents,currency='BRL')=>new Intl.NumberFormat(locale(),{style:'currency',currency}).format(cents/100);
export const amountInput=cents=>new Intl.NumberFormat(locale(),{minimumFractionDigits:2,maximumFractionDigits:2}).format(cents/100);
