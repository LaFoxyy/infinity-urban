import{parseAmount,amountInput}from'../src/InfinityUrban.Web/wwwroot/expense-money.js';
for(const[value,expected]of[['180,01',18001],['0,10',10],['R$ 1.234,56',123456],['1.234',123400],['180.00',18000],['999.999.999,99',99999999999]])if(parseAmount(value)!==expected)throw new Error('Incorrect cents: '+value);
for(const value of ['0','-1','1,234','1e3','1.000.000.000,00','NaN','1,234.56']){let rejected=false;try{parseAmount(value);}catch{rejected=true;}if(!rejected)throw new Error('Invalid money accepted: '+value);}
for(const cents of [1,10,11,99,100,101,18001,123456,99999999999])if(parseAmount(amountInput(cents))!==cents)throw new Error('Currency roundtrip lost cents');
console.log('PASS Brazilian currency parsing, limits and exact-cent roundtrips');
