const $ = (selector, root = document) => root.querySelector(selector);
const icons = {
  calendar: ['M4 5h16v16H4z', 'M16 3v4M8 3v4M4 10h16M8 14h2M14 14h2M8 18h2'],
  reservation: ['M6 4h12v18l-3-2-3 2-3-2-3 2z', 'M9 9h6M9 13h6M9 17h3'],
  building: ['M4 21V7h10v14M14 12h6v9M2 21h20', 'M7 10h1M10 10h1M7 14h1M10 14h1M8 21v-3h3v3M17 15h1M17 18h1'],
  connection: ['M9 15l6-6', 'M7 17l-1 1a4 4 0 0 1-6-6l4-4a4 4 0 0 1 6 0M17 7l1-1a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-6 0'],
  refresh: ['M20 7v5h-5M4 17v-5h5', 'M6 7a7 7 0 0 1 12-1l2 3M4 15l2 3a7 7 0 0 0 12-1'],
  close: ['M6 6l12 12M6 18L18 6'],
  plus: ['M12 5v14M5 12h14'],
  left: ['M15 6l-6 6 6 6'],
  right: ['M9 6l6 6-6 6'],
  layers: ['M12 3L2 8l10 5 10-5z', 'M2 12l10 5 10-5M2 16l10 5 10-5'],
  arrow: ['M5 12h14M13 6l6 6-6 6'],
  check: ['M5 12l4 4L19 6'],
  info: ['M12 8h.01M12 11v6', 'M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0'],
  queue: ['M4 5h16M4 12h16M4 19h10', 'M19 16v6M16 19h6'],
  rate: ['M12 3v18M17 7c-1-3-9-3-9 1 0 5 9 2 9 7 0 4-8 4-10 1'],
  edit: ['M16 3l5 5L8 21H3v-5z', 'M13 6l5 5'],
  shield: ['M12 2l8 4v6c0 5-8 10-8 10S4 17 4 12V6z', 'M8 12l3 3 5-6'],
};

function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('aria-hidden', 'true');
  for (const d of icons[name] || icons.info) {
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', d); svg.append(path);
  }
  return svg;
}
for (const node of document.querySelectorAll('[data-icon]')) node.append(icon(node.dataset.icon));

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null) continue;
    if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'text') node.textContent = value;
    else if (key === 'checked' || key === 'disabled' || key === 'hidden' || key === 'required') node[key] = Boolean(value);
    else node.setAttribute(key, value);
  }
  for (const child of children.flat(Infinity)) if (child !== undefined && child !== null) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  return node;
}
function button(label, onClick, opts = {}) {
  return el('button', {type:'button', class:`button ${opts.primary ? 'primary' : ''} ${opts.small ? 'small' : ''} ${opts.danger ? 'danger' : ''} ${opts.subtle ? 'subtle' : ''}`, onClick: async event => {
    const target = event.currentTarget;
    if (target.disabled) return;
    try { await onClick(event); } catch (error) { toast(error.message, true); }
  }}, opts.icon ? icon(opts.icon) : null, label);
}
function badge(text, kind = '') { return el('span', {class:`badge ${kind}`}, text); }
function money(value) {
  try { return new Intl.NumberFormat('en', {style:'currency', currency:property()?.currency || 'USD', maximumFractionDigits:2, minimumFractionDigits:0}).format(value); }
  catch { return String(value); }
}
function dateLabel(date, options = {month:'short', day:'numeric'}) { return new Date(date + 'T12:00:00').toLocaleDateString('en', options); }
function addDays(date, number) { const value = new Date(date + 'T12:00:00Z'); value.setUTCDate(value.getUTCDate() + number); return value.toISOString().slice(0,10); }
function nowDate() { const date = new Date(); return `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`; }
function timestamp(value) { return value ? new Date(value).toLocaleString('en', {month:'short', day:'numeric', hour:'2-digit', minute:'2-digit'}) : '—'; }
function isCancelled(reservation) { return ['cancelled','canceled'].includes(String(reservation.status).toLowerCase()); }
function isDirect(reservation) { return String(reservation.source).toLowerCase() === 'direct'; }
const state = {view:'calendar', properties:[], selected:localStorage.getItem('stayline.property') || '', status:null, from:nowDate(), calendar:null, reservations:[], sync:null, remote:null, remoteDiscovery:'idle', remoteError:'', loading:false, renderVersion:0, provisioning:new Set()};
function property() { return state.properties.find(item => item.id === state.selected); }
function rooms() { return property()?.roomTypes || []; }
function rates() { return rooms().flatMap(room => (room.ratePlans || []).map(rate => ({...rate, roomName:room.name}))); }
function selectProperty(id) {
  if (state.selected !== id) { state.calendar=null;state.reservations=[];state.sync=null; }
  state.selected=id;localStorage.setItem('stayline.property',id);state.from=propertyToday();updateChrome();
}
function openPropertyCalendar(item) { selectProperty(item.id);return navigate('calendar'); }
function propertyToday() {
  try {
    const parts = new Intl.DateTimeFormat('en', {timeZone:property()?.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone,year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date());
    const part = type => parts.find(item=>item.type===type).value;
    return `${part('year')}-${part('month')}-${part('day')}`;
  } catch { return state.status?.today || nowDate(); }
}

async function api(path, data, method) {
  const options = {headers:{Accept:'application/json'}, cache:'no-store'};
  if (data !== undefined) { options.method = method || 'POST'; options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(data); }
  else if (method) options.method = method;
  let response;
  try { response = await fetch(path, options); } catch { throw new Error('The server could not be reached. Check that Stayline is running and try again.'); }
  let result;
  try { result = await response.json(); } catch { if (!response.ok) throw new Error(`The server returned an error (${response.status}).`); return null; }
  if (!response.ok) { const issue=new Error(result?.error || result?.title || `The request failed (${response.status}).`);issue.status=response.status;throw issue; }
  return result;
}
function toast(message, error = false) {
  const node = el('div', {class:`toast ${error ? 'error' : ''}`, role:error ? 'alert' : 'status'}, message);
  $('#toast-region').append(node); setTimeout(() => node.remove(), error ? 9500 : 6000);
}
function alertError(error) { const node = $('#global-alert'); node.textContent = error?.message || String(error); node.hidden = false; }
function clearAlert() { $('#global-alert').hidden = true; }
function loading() { return el('div', {class:'loading-state'}, el('span',{class:'spinner'}), 'Loading your property…'); }
function hint(text, title) { return el('div', {class:'hint-bar'}, icon('info'), el('div', {}, title ? el('strong', {}, title, ' ') : null, text)); }
function empty(title, description, action, iconName = 'building') {
  return el('div', {class:'empty-state'}, el('div', {class:'empty-art'}, icon(iconName)), el('h2', {}, title), el('p', {}, description), action);
}
function panel(title, subtitle, body, actions, iconName) {
  return el('section', {class:'panel'}, el('div',{class:'panel-header'}, el('div',{}, el('div',{class:'panel-title'}, iconName ? el('span',{},icon(iconName)) : null, el('h3',{}, title)), subtitle ? el('p',{},subtitle) : null), actions), body);
}
function stat(label, value, foot, iconName) {
  return el('div',{class:'stat-card'}, el('div',{class:'stat-label'},label,icon(iconName)),el('div',{class:`stat-value ${typeof value === 'string' && !/^\d+$/.test(value) ? 'text' : ''}`},value),el('div',{class:'stat-foot'},foot));
}
function updateChrome() {
  const select = $('#property-select'); select.replaceChildren();
  if (!state.properties.length) select.append(el('option',{value:''},'No properties yet'));
  else for (const item of state.properties) select.append(el('option',{value:item.id},item.name));
  select.value = state.selected;
  const pending = state.status?.pendingChanges || 0;
  $('#pending-count').hidden = !pending; $('#pending-count').textContent = pending;
  $('#footer-status').textContent = state.status ? `Local workspace · ${state.status.connected ? 'Channex configured' : 'Channex not configured'}` : 'Server unavailable';
  for (const nav of document.querySelectorAll('[data-view]')) {
    nav.classList.toggle('active', nav.dataset.view === state.view);
    if (nav.dataset.view === state.view) nav.setAttribute('aria-current','page'); else nav.removeAttribute('aria-current');
  }
  const headings = {
    calendar:['YOUR OPERATIONS, IN SYNC','Availability & rates','A clear view of every room, rate and date.'],
    reservations:['EVERY STAY, IN ONE PLACE','Reservations','Manage direct bookings and follow incoming channel reservations.'],
    properties:['THE FOUNDATION OF YOUR WORKSPACE','Your properties','Set up rooms, rental units and the rates you sell.'],
    connection:['CONNECTED DISTRIBUTION','Channex & sync','Map your property and follow the changes sent to Channex staging.'],
  };
  const heading = headings[state.view]; $('#page-eyebrow').textContent = heading[0]; $('#page-title').textContent = heading[1]; $('#page-subtitle').textContent = heading[2];
  const actions = $('#page-actions'); actions.replaceChildren();
  if (state.view === 'properties') actions.append(button('Add property', () => propertyModal(), {primary:true,icon:'plus'}));
  if (state.view === 'calendar' && rooms().length) actions.append(button('Edit dates', () => calendarModal(), {primary:true,icon:'edit'}));
  if (state.view === 'reservations' && rooms().some(room => room.ratePlans?.length)) actions.append(button('New reservation', () => reservationModal(), {primary:true,icon:'plus'}));
}
async function refreshBase(version) {
  const [status, properties] = await Promise.all([api('/api/status'), api('/api/properties')]);
  if (version!==undefined && version!==state.renderVersion) return false;
  state.status = status; state.properties = properties;
  if (!property()) state.selected = properties[0]?.id || '';
  localStorage.setItem('stayline.property',state.selected); updateChrome();
  return true;
}
async function discoverStaging() {
  if (state.remoteDiscovery === 'loading') return;
  state.remoteDiscovery = 'loading'; state.remoteError = '';
  try {
    const result = await api('/api/channex/remote');
    state.remote = {...state.remote,...result}; state.remoteDiscovery = 'loaded';
  } catch (error) { state.remoteDiscovery = 'error'; state.remoteError = error.message; }
}
async function refreshView(refreshAll = true) {
  const version = ++state.renderVersion; state.loading = true; clearAlert();
  $('#view-content').replaceChildren(loading());
  $('#refresh-button').disabled = true;
  try {
    if (refreshAll && !await refreshBase(version)) return;
    if (version !== state.renderVersion) return;
    if (['properties','connection'].includes(state.view) && state.status?.connected && state.remoteDiscovery === 'idle') await discoverStaging();
    if (version !== state.renderVersion) return;
    const selected = state.selected, view=state.view, from=state.from;
    let result;
    if (selected && view === 'calendar') result = await api(`/api/properties/${encodeURIComponent(selected)}/calendar?from=${from}&days=14`);
    if (selected && view === 'reservations') result = await api(`/api/properties/${encodeURIComponent(selected)}/reservations`);
    if (selected && view === 'connection') result = await api(`/api/properties/${encodeURIComponent(selected)}/sync`);
    if (version !== state.renderVersion) return;
    if (selected!==state.selected || view!==state.view || (view==='calendar' && from!==state.from)) return;
    if (selected && view === 'calendar') state.calendar=result;
    if (selected && view === 'reservations') state.reservations=result;
    if (selected && view === 'connection') state.sync=result;
    updateChrome(); renderView();
  } catch(error) { if (version === state.renderVersion) { alertError(error); $('#view-content').replaceChildren(empty('Your workspace needs a moment','We could not load this view. Check the message above, then refresh to try again.',button('Try again',()=>refreshView(),{icon:'refresh'}),'connection')); } }
  finally { if (version === state.renderVersion) { state.loading = false; $('#refresh-button').disabled = false; } }
}
function renderView() {
  const views = {calendar:calendarView,reservations:reservationsView,properties:propertiesView,connection:connectionView};
  $('#view-content').replaceChildren(views[state.view]());
}
function onboarding() {
  const steps = [
    ['01','Create your property','Choose hotel or vacation rental, then add your room types or individual units.'],
    ['02','Make the calendar yours','Set the prices, open inventory and restrictions that reflect your operations.'],
    ['03','Connect your channels','Map to a Channex staging property and watch saved updates move through the queue.'],
  ];
  return el('section',{class:'panel'},empty('Every great stay starts here','Your workspace is ready. Add your first property to start managing availability, prices and reservations.',button('Add your first property',()=>propertyModal(),{primary:true,icon:'plus'})),el('div',{class:'setup-steps'},steps.map(([number,title,text])=>el('div',{class:'setup-step'},el('span',{class:'step-number'},number),el('h3',{},title),el('p',{},text)))));
}

function calendarView() {
  if (!property()) return onboarding();
  const picker=calendarPropertyPicker();
  if (!rooms().length) return el('div',{},picker,panel('Bring your property to life',property().name,empty('Add your first room or rental unit','A hotel room type can have multiple rooms. Add each separately sold vacation rental as a unit with an inventory of one.',button('Add a room or unit',()=>roomModal(property()),{primary:true,icon:'plus'}))),hint('Room setup lives in Properties. You can add rate plans there before opening your calendar.'));
  const calendar = state.calendar;
  if (!calendar) return loading();
  const unitCount = rooms().reduce((sum,room)=>sum+room.quantity,0);
  const rateCount = rates().length;
  const stats = el('div',{class:'stats-grid'},stat('Rooms & units',unitCount,`${rooms().length} room ${rooms().length===1?'type':'types'}`, 'building'),stat('Active rate plans',rateCount,`${property().currency} · property currency`,'rate'),stat('Pending changes',state.status?.pendingChanges || 0,'Across your workspace','queue'),stat('Distribution',property().channexId ? 'Property mapped' : 'Not mapped',property().channexId ? 'Review room & rate mapping in Channex' : 'Connect in Channex & sync','connection'));
  const head = el('tr',{},el('th',{scope:'col'},'ROOM TYPE / RATE PLAN'));
  for (const date of calendar.dates) {
    const weekend = [0,6].includes(new Date(date+'T12:00:00').getDay());
    head.append(el('th',{scope:'col',class:`${weekend?'weekend':''} ${date===propertyToday()?'today':''}`},dateLabel(date,{weekday:'short'}),el('b',{},dateLabel(date,{day:'numeric'}))));
  }
  const body = el('tbody');
  for (const room of calendar.rooms) {
    const tr = el('tr',{class:'room-row'},el('td',{},el('div',{class:'room-title'},room.name),el('div',{class:'room-meta'},`${room.quantity} ${room.quantity===1?'unit':'units'} · ${room.occupancy} guests / unit`)));
    for (const day of room.days) {
      const label = `${room.name}, ${dateLabel(day.date)}, ${day.available} available, ${day.capacity} open inventory, ${day.booked} booked. Edit open inventory.`;
      tr.append(el('td',{},el('button',{type:'button',class:'cell-button',title:label,'aria-label':label,onClick:()=>calendarModal({kind:'inventory',roomId:room.id,date:day.date,capacity:day.capacity})},el('span',{class:`inventory-number ${day.available===0?'zero':''}`},day.available),el('span',{class:'cell-sub'},`${day.capacity} open · ${day.booked} booked`))));
    }
    body.append(tr);
    for (const rate of room.ratePlans || []) {
      const rateRow = el('tr',{},el('td',{},el('div',{class:'rate-label'},rate.name)));
      for (const day of rate.days) {
        const restrictions = [];
        if(day.stopSell) restrictions.push(el('span',{class:'restriction closed',title:'Stop sell'},'CLOSED'));
        if(day.minStayArrival>1) restrictions.push(el('span',{class:'restriction',title:'Minimum stay on arrival'},`MIN ${day.minStayArrival}`));
        if(day.minStayThrough>1) restrictions.push(el('span',{class:'restriction',title:'Minimum stay through'},`THR ${day.minStayThrough}`));
        if(day.maxStay>0) restrictions.push(el('span',{class:'restriction',title:'Maximum stay'},`MAX ${day.maxStay}`));
        if(day.closedToArrival) restrictions.push(el('span',{class:'restriction closed',title:'Closed to arrival'},'CTA'));
        if(day.closedToDeparture) restrictions.push(el('span',{class:'restriction closed',title:'Closed to departure'},'CTD'));
        rateRow.append(el('td',{},el('button',{class:'cell-button',type:'button',title:`${rate.name}, ${dateLabel(day.date)}. Edit price and restrictions.`,'aria-label':`${room.name}, ${rate.name}, ${dateLabel(day.date)}, ${money(day.rate)}. Edit price and restrictions.`,onClick:()=>calendarModal({kind:'rate',roomId:room.id,rateId:rate.id,date:day.date,day})},el('span',{class:'price'},money(day.rate)),el('span',{class:'restrictions'},restrictions))));
      }
      body.append(rateRow);
    }
  }
  const table = el('table',{class:'calendar-table'},el('thead',{},head),body);
  const navigation = el('div',{class:'toolbar'},el('span',{class:'date-range'},`${dateLabel(calendar.dates[0])} — ${dateLabel(calendar.dates.at(-1),{month:'short',day:'numeric',year:'numeric'})}`),el('div',{class:'date-nav'},button('Today',()=>{state.from=propertyToday();return refreshView(false);},{small:true}),el('button',{class:'icon-button',type:'button','aria-label':'Previous 14 days',onClick:()=>{state.from=addDays(state.from,-14);refreshView(false);}},icon('left')),el('button',{class:'icon-button',type:'button','aria-label':'Next 14 days',onClick:()=>{state.from=addDays(state.from,14);refreshView(false);}},icon('right'))));
  const grid = panel(property().name,'Select a cell to edit a date. Use Edit dates for ranges and multiple changes.',el('div',{},!rateCount ? el('div',{class:'onboarding-callout'},el('p',{},el('strong',{},'Your inventory is ready. Next, add a rate plan.'),'Rate plans give each room type a sellable price.'),button('Add rate plan',()=>rateModal(rooms()[0]),{small:true,icon:'plus'})) : null,el('div',{class:'calendar-scroll'},table),el('div',{class:'calendar-legend'},el('div',{class:'legend-items'},el('span',{},el('i',{class:'legend-dot'}),'Available'),el('span',{},el('i',{class:'legend-dot closed'}),'No availability')),el('span',{},'Open inventory − active reservations = available rooms'))),navigation);
  const imported = importSuggestion(property().id);
  const review = imported && !property().channexId ? el('div',{class:'onboarding-callout calendar-review'},el('p',{},el('strong',{},'Review your imported calendar before connecting.'),'Check prices, inventory and restrictions, then complete mapping. Saving the mapping sends your saved calendar to Channex.'),button('Review connection',()=>navigate('connection'),{icon:'connection'})) : null;
  return el('div',{},picker,review,stats,grid,hint('Saved changes enter the background queue automatically. A configured connection and complete mapping are required before they can reach Channex.','Changes follow your operations.'));
}

function calendarPropertyPicker() {
  const selector=selectField('Property calendar',state.properties.map(item=>[item.id,`${item.name} · ${item.currency}`]),state.selected,{hint:'Every local property has its own calendar, including properties that are not connected to Channex.'});
  selector.input.addEventListener('change',()=>{selectProperty(selector.input.value);refreshView(false);});
  return el('div',{class:'calendar-property-picker'},selector.wrapper,el('p',{class:'muted tiny'},`${rooms().length} room types / units · ${rates().length} rate plans in ${property().name}`));
}

function propertiesView() {
  const cards = state.properties.map(item=>{
    const roomBlocks = (item.roomTypes || []).map(room=>el('div',{class:'room-block'},el('div',{class:'room-block-head'},el('h4',{},room.name),button('Add rate plan',()=>rateModal(room),{small:true,subtle:true,icon:'plus'})),el('p',{},`${room.quantity} ${room.quantity===1?'unit':'units'} · Maximum ${room.occupancy} guests per unit`),(room.ratePlans || []).length ? room.ratePlans.map(rate=>el('div',{class:'rate-line'},el('span',{},rate.name),el('span',{},new Intl.NumberFormat('en',{style:'currency',currency:item.currency,maximumFractionDigits:2}).format(rate.basePrice),' base price'))) : el('p',{},'No rate plans yet. Add a plan to manage prices for this room.')));
    return el('section',{class:'panel'},el('div',{class:'panel-header'},el('div',{class:'property-card-title'},el('div',{class:'property-icon'},icon('building')),el('div',{},el('h3',{},item.name),el('small',{},item.kind==='rental'?'Vacation rental':'Hotel'),' ')),el('div',{class:'toolbar'},button('Open calendar',()=>openPropertyCalendar(item),{small:true,icon:'calendar'}),button('Add room or unit',()=>roomModal(item),{small:true,icon:'plus'}))),el('div',{class:'property-info'},el('div',{},el('span',{class:'info-label'},'CURRENCY'),el('span',{class:'info-value'},item.currency)),el('div',{},el('span',{class:'info-label'},'TIME ZONE'),el('span',{class:'info-value'},item.timeZoneId)),el('div',{},el('span',{class:'info-label'},'CHANNEX PROPERTY'),badge(item.channexId?'Mapped':'Not mapped',item.channexId?'':'neutral'),item.channexId?el('small',{class:'external-entity-id'},item.channexId):null)),roomBlocks.length?roomBlocks:el('div',{class:'inline-empty'},'Add your first room type or rental unit to start managing inventory.'),el('div',{class:'panel-actions'},provisionAction(item)));
  });
  return el('div',{},state.properties.length?el('p',{class:'section-intro'},'Open any local property’s calendar here. To send a local property to your staging account, use Create in Channex staging. Existing Channex properties can be imported below.'):onboarding(),...cards,stagingPropertiesPanel(),hint('For hotels, a room type can contain several interchangeable rooms. For vacation rentals, add each individual unit with quantity one.','One workspace, both property models.'));
}

function provisionAction(item) {
  if (!item.channexId && importSuggestion(item.id)) return button('Review Channex mapping',()=>{selectProperty(item.id);return navigate('connection');},{small:true,icon:'connection'});
  const action=button(item.channexId?'Update Channex setup':'Create in Channex staging',()=>provisionModal(item),{small:true,icon:'connection'});
  const localRooms=item.roomTypes||[];
  const issue=!state.status?.connected||!state.status?.enabled?'Configure and enable Channex staging on the server.':!localRooms.length?'Add a room type or rental unit first.':localRooms.some(room=>!room.ratePlans?.length)?'Add at least one rate plan to every room type or unit.':state.provisioning.has(item.id)?'Channex setup is already running.':'';
  action.disabled=Boolean(issue);action.title=issue||'Review the property, rooms and rate plans before sending them to Channex staging';return action;
}

function provisionModal(item) {
  if (state.provisioning.has(item.id)) return;
  const localRooms=item.roomTypes||[],localRates=localRooms.flatMap(room=>room.ratePlans||[]);
  const summary=el('div',{class:'import-preview-details'},el('h3',{},item.name),el('p',{class:'section-intro'},`${item.currency} · ${item.timeZoneId}`),el('div',{class:'request-meta'},el('span',{},`${localRooms.length} room types / units`),el('span',{},`${localRooms.reduce((count,room)=>count+room.quantity,0)} total units`),el('span',{},`${localRates.length} rate plans`)),item.channexId?el('p',{class:'form-note'},'Linked Channex property: ',el('code',{},item.channexId)):null,el('div',{class:'preview-rooms'},localRooms.map(room=>el('div',{class:'preview-room'},el('h4',{},room.name),el('p',{class:'muted tiny'},`${room.quantity} units · ${room.occupancy} guests per unit${room.channexId?' · already linked':''}`),(room.ratePlans||[]).map(rate=>el('div',{class:'rate-line'},el('span',{},rate.name),el('span',{},`${Number(rate.basePrice).toLocaleString('en',{maximumFractionDigits:2})} ${item.currency} base price${rate.channexId?' · already linked':''}`)))))));
  const error=el('div',{class:'alert error',hidden:true,role:'alert'});
  const cancel=button('Go back',closeModal);
  const submit=button(item.channexId?'Update setup and sync':'Create setup and sync',async event=>{
    if (state.provisioning.has(item.id)) return;
    error.hidden=true;state.provisioning.add(item.id);cancel.disabled=true;
    try {
      await busy(event.currentTarget,async()=>{
        const result=await api(`/api/properties/${encodeURIComponent(item.id)}/channex/provision`,{});
        selectProperty(item.id);state.sync=null;
        await refreshBase();
        state.remoteDiscovery='idle';await discoverStaging();
        state.provisioning.delete(item.id);closeModal();
        const created=(result.createdProperties||0)+(result.createdRoomTypes||0)+(result.createdRatePlans||0);
        toast(`${created?`${created} Channex ${created===1?'record':'records'} created.`:'Channex setup verified.'} ${result.queuedChanges||0} calendar changes queued. Follow the API responses and task IDs in sync history.`);
        await navigate('connection');
      });
    } catch(issue) {
      try { await refreshBase();state.remoteDiscovery='idle';await discoverStaging(); } catch { /* Keep the original API error visible. */ }
      error.textContent=issue.message;error.hidden=false;
      error.append(el('p',{},'Saved Channex links are retained. Check the setup requests in sync history and reconcile any uncertain result with the Channex staging account before trying again.'));
      submit.textContent='Setup request failed';submit.disabled=true;
    } finally { state.provisioning.delete(item.id);cancel.disabled=false; }
  },{primary:true,icon:'connection'});
  submit.disabled=!localRooms.length||localRooms.some(room=>!room.ratePlans?.length);
  openModal(item.channexId?'Review Channex setup':'Create this property in Channex staging',el('div',{},el('p',{class:'form-note'},item.channexId?'Create any missing room types and rate plans under this linked Channex property. Existing links are reused.':'Create this local property, its room types and its rate plans in your Channex staging account. Successful API responses save the corresponding links in Stayline.'),summary,el('div',{class:'alert warning'},'This also queues an initial 500-day publish of this property’s saved availability, prices and restrictions. Review its calendar and bookings before continuing. The background worker sends the calendar after setup succeeds.'),error,el('div',{class:'form-actions'},cancel,submit)),true);
}

function importSuggestion(id) { return state.properties.find(item=>item.id===id)?.suggestedChannexId || localStorage.getItem(`stayline.import.${id}`) || ''; }
function stagingPropertiesPanel() {
  const reload = button(state.remoteDiscovery==='error'?'Try staging again':'Refresh staging properties',async event=>{
    await busy(event.currentTarget,async()=>{await discoverStaging();renderView();});
  },{small:true,icon:'refresh'});
  reload.disabled = !state.status?.connected || state.remoteDiscovery==='loading';
  let body;
  if (!state.status?.connected) body=el('div',{class:'inline-empty'},'Configure your Channex staging key on the server to browse the properties available to your account.');
  else if (state.remoteDiscovery==='error') body=el('div',{class:'panel-body'},el('div',{class:'alert error',role:'alert'},state.remoteError),el('p',{class:'form-note'},'Your saved local properties are still available. Retry staging discovery when the connection is ready.'));
  else if (!state.remote || state.remoteDiscovery==='loading') body=el('div',{class:'inline-empty'},el('span',{class:'spinner'}),' Loading Channex staging properties…');
  else if (!state.remote.properties?.length) body=el('div',{class:'inline-empty'},'No staging properties are visible to this API key. Check its property access in Channex, then refresh this list.');
  else body=el('div',{class:'staging-property-list'},state.remote.properties.map(remote=>{
    const mapped=state.properties.find(item=>item.channexId===remote.id);
    const imported=state.properties.find(item=>importSuggestion(item.id)===remote.id);
    const local=mapped||imported;
    const action=local?button(mapped?'Open connection':'Review local calendar',()=>{selectProperty(local.id);return navigate(mapped?'connection':'calendar');},{small:true,icon:mapped?'connection':'calendar'}):button('Import into PMS',()=>importPreviewModal(remote.id),{small:true,primary:true,icon:'plus'});
    return el('article',{class:'staging-property-card'},el('div',{},el('h4',{},remote.name),el('p',{},`${remote.currency} · Channex staging`),local?el('p',{class:'muted tiny'},`Local property: ${local.name}`):null),el('div',{class:'staging-property-actions'},badge(mapped?'Mapped':imported?'Imported · review calendar':'Available to import',mapped?'':imported?'warning':'neutral'),action));
  }));
  return panel('Channex staging properties','Browse your staging account and import a property for local operation. Importing does not send calendar updates.',body,reload,'connection');
}

async function importPreviewModal(externalId) {
  const content=el('div',{},el('p',{class:'form-note'},'Loading the property, rooms, rates and saved calendar from Channex staging…'));
  openModal('Review staging import',content,true);
  let preview;
  try { preview=await api(`/api/channex/import-preview?propertyId=${encodeURIComponent(externalId)}`); }
  catch (issue) {
    if ($('#modal').open && $('#modal-content').firstChild===content) content.replaceChildren(el('div',{class:'alert error',role:'alert'},issue.message),el('p',{class:'form-note'},'The import preview could not be prepared. No local property was created and no calendar updates were sent.'),el('div',{class:'form-actions'},button('Cancel',closeModal),button('Retry preview',()=>importPreviewModal(externalId),{primary:true,icon:'refresh'})));
    return;
  }
  if (!$('#modal').open || $('#modal-content').firstChild!==content) return;
  const roomList=preview.rooms||[];
  const planCount=roomList.reduce((count,room)=>count+(room.ratePlans||[]).length,0);
  const missingPriceInputs=new Map();
  const warnings=preview.warnings?.length?el('div',{class:'alert warning'},el('strong',{},'Review before importing'),el('ul',{class:'preview-warning-list'},preview.warnings.map(text=>el('li',{},text)))):null;
  const previewRooms=el('div',{class:'preview-rooms'},roomList.map(room=>{
    const rateLines=(room.ratePlans||[]).map(rate=>{
      const missingDates=[...(rate.missingPriceDates||[])].sort();
      const missingCount=rate.missingPriceCount||missingDates.length;
      const summary=el('div',{class:'rate-line'},el('span',{},rate.name),el('span',{},rate.basePrice===null||rate.basePrice===undefined?'No saved positive price':`${new Intl.NumberFormat('en',{maximumFractionDigits:2}).format(rate.basePrice)} ${preview.currency} ${missingCount?'from priced dates':'starting price'}`));
      if (!missingCount) return summary;
      const missingRange=missingDates.length?` (${missingDates[0]} to ${missingDates[missingDates.length-1]})`:'';
      const price=field(`${room.name} · ${rate.name}: local price for unpriced dates (${preview.currency})`,{type:'number',value:rate.basePrice!==null&&rate.basePrice!==undefined&&Number(rate.basePrice)>0?Number(rate.basePrice).toFixed(2):'',attrs:{min:'0.01',max:'1000000',step:'0.01',inputmode:'decimal'},hint:'Required. Enter a positive price with at most two decimal places.'});
      price.input.addEventListener('input',()=>price.input.setCustomValidity(''));
      missingPriceInputs.set(rate.externalId,price.input);
      return el('div',{class:'preview-unpriced-rate'},summary,el('p',{class:'form-note'},`${missingCount} unpriced ${missingCount===1?'date':'dates'}${missingRange}. Choose the price to save for those dates in your local calendar.`),price.wrapper);
    });
    return el('div',{class:'preview-room'},el('h4',{},room.name),el('p',{class:'muted tiny'},`${room.quantity} units · ${room.occupancy} guests per unit`),rateLines);
  }));
  const details=el('div',{class:'import-preview-details'},el('h3',{},preview.name),el('p',{class:'section-intro'},`${preview.currency} · ${preview.timeZoneId}`),el('div',{class:'request-meta'},el('span',{},`${roomList.length} room types / units`),el('span',{},`${roomList.reduce((sum,room)=>sum+room.quantity,0)} total units`),el('span',{},`${planCount} rate plans`),el('span',{},`${preview.days||500}-day calendar snapshot`)),previewRooms);
  const error=el('div',{class:'alert error',hidden:true,role:'alert'});
  const submit=button('Import local property',async event=>{
    error.hidden=true;
    try {
      const missingPriceDefaults={};
      for (const [rateId,input] of missingPriceInputs) {
        const price=Number(input.value);
        input.setCustomValidity(/^(?:\d+(?:\.\d{1,2})?|\.\d{1,2})$/.test(input.value)&&Number.isFinite(price)&&price>0&&price<=1000000?'':'Enter a price above zero and no more than 1,000,000, with at most two decimal places.');
        if (!input.reportValidity()) return;
        missingPriceDefaults[rateId]=price;
      }
      await busy(event.currentTarget,async()=>{
        const result=await api('/api/channex/import',{propertyId:externalId,...(missingPriceInputs.size?{missingPriceDefaults}:{})});
        state.selected=result.propertyId;localStorage.setItem('stayline.property',result.propertyId);localStorage.setItem(`stayline.import.${result.propertyId}`,result.externalPropertyId||externalId);
        state.calendar=null;state.sync=null;await refreshBase();state.from=propertyToday();closeModal();
        toast(result.alreadyImported?'This staging property was already imported. Review its local calendar.':`${result.name} imported locally. Review the calendar before connecting.`);
        await navigate('calendar');
      });
    } catch(issue) { error.textContent=issue.message;error.hidden=false; }
  },{primary:true,icon:'plus'});
  submit.disabled = !roomList.length || roomList.some(room=>!room.ratePlans?.length);
  content.replaceChildren(el('p',{class:'form-note'},'Create a new local property using this staging snapshot. Your existing properties stay as they are. You will review the calendar before mapping and publishing it to Channex.'),details,missingPriceInputs.size?el('p',{class:'alert info'},'These prices initialize the unpriced dates in your local draft. Channex is updated only after you confirm mapping.'):null,warnings,error,submit.disabled?el('p',{class:'form-note'},'Each room needs at least one rate plan before it can be imported. Complete the staging setup and try again.'):null,el('div',{class:'form-actions'},button('Cancel',closeModal),submit));
}

function reservationsView() {
  if (!property()) return onboarding();
  if (!rooms().some(room=>room.ratePlans?.length)) return panel('Reservations',property().name,empty('Get your property ready for bookings','Add at least one room or unit and a rate plan before recording a reservation.',button('Open properties',()=>navigate('properties'),{primary:true,icon:'building'}),'reservation'));
  const active = state.reservations.filter(item=>!isCancelled(item));
  const upcoming = active.filter(item=>item.arrival >= propertyToday());
  const stats = el('div',{class:'stats-grid'},stat('Active reservations',active.length,'Excludes cancellations','reservation'),stat('Upcoming arrivals',upcoming.length,'From today onwards','calendar'),stat('Direct bookings',active.filter(isDirect).length,'Managed in your workspace','building'),stat('Channel bookings',active.filter(item=>!isDirect(item)).length,'Managed by the booking channel','connection'));
  const table = el('table',{class:'data-table'},el('thead',{},el('tr',{},['GUEST / ACCOMMODATION','DATES','SOURCE','STATUS',''].map(title=>el('th',{scope:'col'},title)))));
  const body = el('tbody');
  for (const item of [...state.reservations].sort((a,b)=>b.arrival.localeCompare(a.arrival))) {
    const roomNames = (item.stays || []).map(stay=>`${rooms().find(room=>room.id===stay.roomTypeId)?.name || 'Room'} × ${stay.quantity}`).join(', ');
    body.append(el('tr',{},el('td',{},el('div',{class:'guest-name'},item.guestName),el('div',{class:'muted tiny',style:'margin-top:6px'},roomNames)),el('td',{},`${dateLabel(item.arrival)} → ${dateLabel(item.departure)}`,el('div',{class:'muted tiny',style:'margin-top:6px'},`${Math.round((new Date(item.departure)-new Date(item.arrival))/86400000)} nights`)),el('td',{},badge(item.source,isDirect(item)?'neutral':'')),el('td',{},badge(item.status,isCancelled(item)?'neutral':'')),el('td',{},isDirect(item)&&!isCancelled(item)?button('Manage',()=>reservationModal(item),{small:true,icon:'edit'}):el('span',{class:'muted tiny'},isDirect(item)?'Cancelled':'Channel managed'))));
  }
  table.append(body);
  const main = state.reservations.length?el('div',{class:'table-scroll'},table):empty('Your next guest is on the way','Record a direct booking here. Once connected, incoming Channex reservations also appear in this list and adjust availability.',button('Create a reservation',()=>reservationModal(),{primary:true,icon:'plus'}),'reservation');
  return el('div',{},stats,panel('Reservation book',`${property().name} · ${state.reservations.length} total reservations`,main),hint('Arrival dates are included and departure dates are excluded when calculating booked inventory. Channel reservations are read-only here; manage changes at their source.'));
}

function connectionView() {
  const configured = state.status?.connected && state.status?.enabled;
  const banner = el('div',{class:'connection-banner'},el('div',{class:'connection-badge'},icon('connection')),el('div',{},el('h3',{},configured?'Channex staging is configured':'Connect your staging account'),el('p',{},configured?'The worker sends mapped availability, rates and restrictions when you save operational changes.':'Your property works locally. Configure the server to start sending real updates to Channex staging.')),badge(configured?'Worker enabled':'Local workspace',configured?'':'neutral'));
  const configuration = !configured?panel('Server configuration','Credentials stay on your server.',el('div',{class:'panel-body'},el('p',{class:'form-note'},'Use the setup instructions included with this project. Restart the server after updating your environment variables.'),el('ul',{class:'config-list'},el('li',{},el('code',{},'Channex__Enabled=true'),' enables the background worker.'),el('li',{},el('code',{},'Channex__ApiKey'),' sets your staging API key.'),el('li',{},el('code',{},'Channex__WebhookSecret'),' protects incoming booking notifications.')),el('div',{class:'divider'}),hint('Credentials are never collected by this page. The mapping controls read your staging account through the backend.'))):!state.status.webhookConfigured?el('div',{class:'alert info'},'Booking webhooks are not configured. Incoming bookings can still arrive through the booking feed. Use “Check incoming bookings” below to check now.'):null;
  if (!property()) return el('div',{},banner,configuration,panel('Start with a local property','No properties are saved in Stayline yet.',el('div',{class:'panel-body'},el('p',{class:'form-note'},'Import a Channex staging property below to bring its rooms, rates and calendar into Stayline, or create a property in Properties.'),button('Create a local property',()=>propertyModal(),{icon:'plus'}))),stagingPropertiesPanel());
  const mappingContainer = el('div',{class:'panel-body'});
  mappingContainer.append(el('p',{class:'form-note'},'Select the matching staging property, then match every room and rate plan. Saving a complete mapping queues an initial 500-day sync of your saved PMS calendar.'));
  const remote = state.remote;
  if (!remote || state.remoteDiscovery==='error') mappingContainer.append(state.remoteError?el('div',{class:'alert error',role:'alert'},state.remoteError):null,button('Load staging properties',async event=>{
    await busy(event.currentTarget,async()=>{await discoverStaging();renderView();});
  },{icon:'refresh',primary:configured}));
  else {
    const external = selectField('Channex property',(remote.properties||[]).map(item=>[item.id,`${item.name} · ${item.currency}`]),property().channexId||importSuggestion(property().id)||'',{placeholder:'Choose a staging property'});
    const mappings = el('div'), readinessNote=el('div',{class:'mapping-readiness','aria-live':'polite'}), currencyNote=el('div',{class:'mapping-currency'}), metadataError=el('div',{class:'alert error',hidden:true,role:'alert'});
    let loadedId = remote.loadedPropertyId || '';
    const roomSelects = new Map(), rateSelects = new Map();
    const mappingIssues = ()=>{
      const issues=[];const selected=(remote.properties||[]).find(item=>item.id===external.input.value);
      if (!configured) issues.push('Enable and configure the staging connection on the server.');
      if (!selected) issues.push('Choose a Channex staging property.');
      else if (String(selected.currency).toUpperCase()!==String(property().currency).toUpperCase()) issues.push('Property currencies must match. Import the staging property below to create a separate local property in its currency.');
      if (!rooms().length) issues.push('Add a local room or unit and its rate plan, or import the staging property below.');
      else if (rooms().some(room=>!room.ratePlans?.length)) issues.push('Every local room or unit needs at least one rate plan.');
      if (selected && loadedId!==selected.id) issues.push('Load the selected property’s rooms and rates.');
      else if (rooms().length && (rooms().some(room=>!roomSelects.get(room.id)?.value)||rates().some(rate=>!rateSelects.get(rate.id)?.value))) issues.push('Choose a Channex match for every local room and rate plan.');
      for (const [label,selects] of [['room',roomSelects],['rate plan',rateSelects]]) {const values=[...selects.values()].map(input=>input.value).filter(Boolean);if(new Set(values).size!==values.length) issues.push(`Each Channex ${label} can only be matched once.`);}
      return issues;
    };
    const updateReadiness = ()=>{
      const selected=(remote.properties||[]).find(item=>item.id===external.input.value);
      const mismatch=selected&&String(selected.currency).toUpperCase()!==String(property().currency).toUpperCase();
      currencyNote.replaceChildren(selected?el('div',{class:`alert ${mismatch?'warning':'info'}`},`Local property: ${property().currency} · Channex property: ${selected.currency}. `,mismatch?'These currencies cannot be mapped. Import the staging property as a new local property; your existing property keeps its currency.':'The currencies match.'):null);
      const issues=mappingIssues();saveButton.disabled=issues.length>0;saveButton.title=issues[0]||'Review and save this mapping';
      loadButton.disabled=!state.status?.connected||!external.input.value;
      readinessNote.replaceChildren(issues.length?el('ul',{class:'readiness-list'},issues.map(issue=>el('li',{},issue))):el('p',{class:'ready-note'},'All rooms and rates are matched. Review the calendar before saving this connection.'));
    };
    const fillMappings = ()=>{
      mappings.replaceChildren();roomSelects.clear();rateSelects.clear();
      if(!external.input.value || loadedId!==external.input.value) { updateReadiness();return; }
      if(!rooms().length) { updateReadiness();return; }
      mappings.append(el('p',{class:'mapping-group'},'ROOM TYPES / UNITS'));
      for(const room of rooms()) {
        const options = (remote.roomTypes||[]).filter(item=>item.propertyId===loadedId);
        const input = optionSelect(options.map(item=>[item.id,item.name]),room.channexId||room.suggestedChannexId||'','Not mapped');
        input.setAttribute('aria-label',`Channex room for ${room.name}`);roomSelects.set(room.id,input);
        const row = el('div',{class:'mapping-row'},el('div',{class:'mapping-label'},room.name,el('small',{},`${room.quantity} units`)),input);mappings.append(row);
        input.addEventListener('change',()=>{
          for(const rate of room.ratePlans||[]) {
            const rateInput=rateSelects.get(rate.id); const old=rateInput?.value;
            if(rateInput){rateInput.replaceChildren(...optionSelect((remote.ratePlans||[]).filter(item=>item.roomTypeId===input.value).map(item=>[item.id,item.name]),old,'Not mapped').children);}
          }
          updateReadiness();
        });
      }
      mappings.append(el('p',{class:'mapping-group'},'RATE PLANS'));
      for(const room of rooms()) for(const rate of room.ratePlans||[]) {
        const input = optionSelect((remote.ratePlans||[]).filter(item=>item.roomTypeId===roomSelects.get(room.id).value).map(item=>[item.id,item.name]),rate.channexId||rate.suggestedChannexId||'','Not mapped');
        input.setAttribute('aria-label',`Channex rate for ${room.name}, ${rate.name}`);rateSelects.set(rate.id,input);
        input.addEventListener('change',updateReadiness);
        mappings.append(el('div',{class:'mapping-row'},el('div',{class:'mapping-label'},rate.name,el('small',{},room.name)),input));
      }
      updateReadiness();
    };
    const loadButton=button('Load rooms & rates',async event=>{
      if(!external.input.value)throw new Error('Choose a Channex property first.');
      metadataError.hidden=true;
      await busy(event.currentTarget,async()=>{
        const selectedId=external.input.value;external.input.disabled=true;
        try {
          const result=await api(`/api/channex/remote?propertyId=${encodeURIComponent(selectedId)}`);
          Object.assign(remote,result,{loadedPropertyId:selectedId});loadedId=selectedId;fillMappings();
        } catch(issue) { metadataError.textContent=issue.message;metadataError.hidden=false; }
        finally {external.input.disabled=false;updateReadiness();}
      });
    },{small:true,icon:'refresh'});
    const saveButton=button('Save mapping',()=>{
      const issues=mappingIssues();if(issues.length)throw new Error(issues[0]);
      const localId=state.selected,selected=(remote.properties||[]).find(item=>item.id===external.input.value);
      const payload={channexId:external.input.value,rooms:rooms().map(room=>({id:room.id,channexId:roomSelects.get(room.id).value})),rates:rates().map(rate=>({id:rate.id,channexId:rateSelects.get(rate.id).value}))};
      confirmationModal('Connect and publish your calendar?',`Connect ${property().name} (${property().currency}) to ${selected.name} (${selected.currency}). Saving queues 500 days of this PMS property’s saved prices, available inventory and restrictions, replacing the corresponding values in Channex staging. Review the calendar and reconcile existing bookings before continuing.`,async()=>{
        const result=await api(`/api/properties/${localId}/mapping`,payload,'PUT');
        toast(result?.fullSyncQueued?'Mapping saved and an initial calendar sync queued.':'Mapping saved.');
      },'Save mapping and sync');
    },{primary:true,icon:'check'});
    external.input.addEventListener('change',()=>{metadataError.hidden=true;fillMappings();});
    mappingContainer.append(el('div',{class:'mapping-top'},external.wrapper,loadButton),currencyNote,metadataError,mappings,readinessNote,el('div',{class:'form-actions'},button('Review calendar',()=>navigate('calendar'),{small:true,subtle:true}),button('Clear local mapping',()=>clearMappingModal(),{small:true,subtle:true}),saveButton));fillMappings();
  }
  const setup=!importSuggestion(property().id)?panel(property().channexId?'Channex property setup':'Send this local property to Channex',property().name,el('div',{class:'panel-body'},el('p',{class:'form-note'},property().channexId?'Add missing local room types and rate plans to the linked Channex property, then publish the saved calendar. Review the API requests in sync history.':'Create the property, its room types and its rate plans in your staging account, then publish the saved calendar. Use the mapping panel when the corresponding property already exists in Channex.'),provisionAction(property()))):null;
  const mapping=panel('Property mapping',property().name,mappingContainer,null,'layers');
  const sync=state.sync||{pending:0,attempts:[],receipts:[]};
  const fallbackIssues=[];
  if(!configured)fallbackIssues.push('Configure and enable the Channex staging connection.');
  if(!property().channexId)fallbackIssues.push('Save a complete property, room and rate mapping first.');
  if(!rooms().length||rooms().some(room=>!room.ratePlans?.length))fallbackIssues.push('Add rooms and at least one rate plan for each room.');
  if(rooms().some(room=>!room.channexId)||rates().some(rate=>!rate.channexId))fallbackIssues.push('Complete every room and rate mapping.');
  const readiness=sync.readiness||{ready:!fallbackIssues.length,issues:fallbackIssues};
  const fullSyncButton=button('Initial / recovery full sync',()=>fullSyncModal(),{icon:'refresh'});fullSyncButton.disabled=!readiness.ready;fullSyncButton.title=readiness.ready?'Send the saved 500-day PMS calendar':(readiness.issues||[]).join(' ');
  const retryButton=button('Retry failed changes',async event=>{
    await busy(event.currentTarget,async()=>{await api(`/api/properties/${state.selected}/retry`,{});toast('Failed changes are queued for retry.');await refreshView();});
  },{small:true});retryButton.disabled=!readiness.ready;
  const tools=panel('Sync controls','Initial setup and recovery.',el('div',{class:'panel-body'},el('p',{class:'form-note'},'Normal calendar and reservation changes send only the dates that changed. A full sync sends 500 days of your saved calendar for onboarding or recovery.'),el('div',{class:'toolbar'},fullSyncButton,retryButton),!readiness.ready?el('div',{class:'sync-readiness'},el('strong',{},'Calendar sync is not ready'),el('ul',{class:'readiness-list'},(readiness.issues||fallbackIssues).map(issue=>el('li',{},issue)))):el('p',{class:'ready-note'},'Calendar sync is ready.'),el('div',{class:'divider'}),el('div',{class:'request-meta'},el('span',{},`${sync.pending} pending changes`),el('span',{},state.status?.webhookConfigured?'Booking webhooks configured':'Booking webhooks: not configured'))));
  const attempts=sync.attempts?.length?el('div',{},sync.attempts.map(item=>{
    const status=String(item.status||'').toLowerCase();const failed=['failed','error','rejected','expired'].some(term=>status.includes(term));const warning=['warning','pending','retry','sending','uncertain'].some(term=>status.includes(term));const imported=status.includes('import')||String(item.endpoint).toLowerCase().includes('import');const metadata=status.startsWith('provision-')||status==='provisioned';
    let entityDetail;try { entityDetail=metadata&&item.detail?JSON.parse(item.detail):null; } catch { entityDetail=null; }
    const summary=el('summary',{},el('div',{class:'attempt-title'},el('strong',{},imported?'Staging import (local only)':item.endpoint),el('span',{class:'muted tiny'},timestamp(item.createdAt))),badge(item.status,failed?'error':warning?'warning':imported?'neutral':''),el('span',{class:'muted tiny'},imported?'No calendar published':metadata?'Property setup':`${item.changeCount} changes`));
    const detail=el('div',{class:'sync-detail'},el('div',{class:'request-meta'},el('span',{},`HTTP ${item.httpStatus||'—'}`),imported?el('span',{},'Local import · no outgoing task IDs'):metadata?el('span',{},'Synchronous setup response'):el('span',{},`${(item.taskIds||[]).length} task IDs`)),entityDetail?.externalId?copyableId('Channex entity ID',entityDetail.externalId):null,item.detail?el('p',{},item.detail):null,imported?null:(item.taskIds||[]).map(taskId=>copyableId('Channex task ID',taskId)),!imported&&!metadata?requestSummary(item):null,el('p',{class:'muted tiny'},imported?'Saved import details':'Saved request payload'),el('pre',{},pretty(item.requestJson)));
    return el('details',{class:'sync-attempt'},summary,detail);
  })):el('div',{class:'inline-empty'},'No requests have been sent yet. Saved changes appear here after the worker attempts to send them.');
  const checkBookings=button('Check incoming bookings',async event=>{await busy(event.currentTarget,async()=>{await api('/api/channex/bookings/check',{});toast('Incoming booking feed checked. Review receipt stages and acknowledgment below.');await refreshView();});},{small:true,icon:'refresh'});checkBookings.disabled=!configured;
  const receipts=sync.receipts?.length?el('div',{},sync.receipts.map(item=>{
    const status=String(item.revisionStatus||'').toLowerCase();const stage={new:'New booking',modified:'Modified booking',cancelled:'Cancelled booking',canceled:'Cancelled booking'}[status]||'Revision stage not recorded';
    const source=String(item.source||'').toLowerCase();const sourceLabel=source==='feed'?'Feed':source==='webhook'?'Webhook → feed':'Source not recorded';
    return el('div',{class:'receipt-row'},el('div',{},el('div',{class:'receipt-stage'},el('strong',{},stage),badge(sourceLabel,'neutral')),item.externalBookingId?copyableId('Channex booking ID',item.externalBookingId):el('p',{class:'muted tiny'},'Booking ID was not recorded for this older receipt.'),copyableId('Booking revision ID',item.id),el('div',{class:'muted tiny',style:'margin-top:8px'},`Received ${timestamp(item.receivedAt)} · Processed ${timestamp(item.processedAt)}`),item.error?el('p',{class:'receipt-error',role:'alert'},item.error):null),badge(item.ackedAt?'Acknowledged':item.error?'Needs attention':'Awaiting acknowledgement',item.error?'error':item.ackedAt?'':'warning'));
  })):el('div',{class:'inline-empty'},'No incoming booking receipts yet. Check the feed after creating or changing a booking in Channex. Each saved revision has its own receipt and acknowledgment.');
  return el('div',{},banner,configuration,setup,el('div',{class:'two-column'},el('div',{},mapping),el('div',{},tools,panel('Booking receipts','Incoming reservation processing.',receipts,checkBookings))),stagingPropertiesPanel(),panel('Sync history','Expand a request to inspect its dates, selected fields and real Channex task IDs.',attempts,button('Refresh',()=>refreshView(),{small:true,icon:'refresh'}),'queue'),hint('This pre-alpha runs against staging. Certification requires the full scenario checklist and a live review by Channex; this application does not grant or guarantee certification.'));
}
function copyableId(label,value) {
  const code=el('code',{class:'task-id'},String(value));
  const copy=button('Copy',async()=>{
    if(navigator.clipboard?.writeText){await navigator.clipboard.writeText(String(value));toast(`${label} copied.`);}
    else {const range=document.createRange();range.selectNodeContents(code);const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);toast('ID selected. Press Ctrl+C to copy.');}
  },{small:true,subtle:true});copy.setAttribute('aria-label',`Copy ${label}`);copy.title=`Copy ${label}`;
  return el('div',{class:'copyable-id'},el('div',{},el('span',{class:'id-label'},label),code),copy);
}
const restrictionLabels={rate:'Nightly price',min_stay_arrival:'Minimum stay on arrival',min_stay_through:'Minimum stay through',max_stay:'Maximum stay',stop_sell:'Stop sell',closed_to_arrival:'Closed to arrival',closed_to_departure:'Closed to departure',availability:'Available rooms'};
function requestSummary(attempt) {
  let values;try {values=(typeof attempt.requestJson==='string'?JSON.parse(attempt.requestJson):attempt.requestJson)?.values;}catch{return el('p',{class:'muted tiny'},'Summary unavailable: this older payload preview is incomplete.');}
  if(!Array.isArray(values)||!values.length)return null;
  const body=el('tbody');
  for(const value of values.slice(0,200)){
    const room=rooms().find(item=>item.channexId===value.room_type_id);
    const rate=rates().find(item=>item.channexId===value.rate_plan_id);
    const entity=rate?`${rate.roomName} · ${rate.name}`:room?.name||`Unmapped ${value.rate_plan_id?'rate plan':'room'} ${value.rate_plan_id||value.room_type_id||''}`;
    const fields=Object.entries(restrictionLabels).filter(([name])=>Object.hasOwn(value,name)).map(([name,label])=>`${label}: ${typeof value[name]==='boolean'?(value[name]?'Yes':'No'):name==='rate'?money(value[name]):value[name]}`);
    body.append(el('tr',{},el('td',{},entity),el('td',{},value.date||`${value.date_from||'—'} → ${value.date_to||'—'}`),el('td',{},fields.join(' · '))));
  }
  return el('div',{class:'request-summary'},el('p',{},el('strong',{},'Sent dates and fields')),el('div',{class:'table-scroll'},el('table',{class:'data-table change-summary-table'},el('thead',{},el('tr',{},['ROOM / RATE PLAN','DATE / INCLUSIVE RANGE','SENT FIELDS'].map(title=>el('th',{scope:'col'},title)))),body)),values.length>200?el('p',{class:'muted tiny'},`Showing 200 of ${values.length} payload entries. The saved request below contains the available payload preview.`):null);
}
function pretty(value) { if(!value)return 'No payload recorded.';try{return JSON.stringify(typeof value==='string'?JSON.parse(value):value,null,2);}catch{return String(value);} }

async function busy(node, action) { node.disabled=true;try{return await action();}finally{node.disabled=false;} }
function openModal(title,content,wide=false) {
  const modal=$('#modal');modal.classList.toggle('wide',wide);$('#modal-title').textContent=title;$('#modal-eyebrow').textContent=property()?.name||'YOUR WORKSPACE';$('#modal-content').replaceChildren(content);if(!modal.open)modal.showModal();
}
function closeModal() { $('#modal').close(); }
function optionSelect(options,value='',placeholder) {
  const input=el('select');if(placeholder!==undefined)input.append(el('option',{value:''},placeholder));for(const [id,label]of options)input.append(el('option',{value:id},label));input.value=value;return input;
}
function field(label,options={}) {
  const id=`field-${Math.random().toString(36).slice(2,10)}`;
  const input=el('input',{id,type:options.type||'text',required:options.required!==false,...options.attrs});
  if(options.value!==undefined)input.value=options.value;
  const wrapper=el('div',{class:`field ${options.full?'full':''}`},el('label',{for:id},label),input,options.hint?el('small',{},options.hint):null);
  return {input,wrapper};
}
function selectField(label,options,value,config={}) {
  const id=`field-${Math.random().toString(36).slice(2,10)}`;const input=optionSelect(options,value,config.placeholder);input.id=id;input.required=config.required!==false;
  const wrapper=el('div',{class:`field ${config.full?'full':''}`},el('label',{for:id},label),input,config.hint?el('small',{},config.hint):null);return{input,wrapper};
}
function formShell(fields,onSave,submitLabel='Save',note,extraActions) {
  const form=el('form');const error=el('div',{class:'alert error',role:'alert',hidden:true});
  const submit=el('button',{type:'submit',class:'button primary'},icon('check'),submitLabel);
  form.append(note?el('p',{class:'form-note'},note):null,error,el('div',{class:'form-grid'},fields),el('div',{class:'form-actions'},extraActions,button('Cancel',closeModal),submit));
  form.addEventListener('submit',async event=>{
    event.preventDefault();if(submit.disabled)return;error.hidden=true;
    await busy(submit,async()=>{try{await onSave();closeModal();await refreshView();}catch(issue){error.textContent=issue.message;error.hidden=false;error.scrollIntoView({block:'nearest'});}});
  });return form;
}
function propertyModal() {
  const name=field('Property name',{full:true,attrs:{maxlength:120,placeholder:'e.g. Seaside House'}});
  const kind=selectField('Property type',[['hotel','Hotel'],['rental','Vacation rental']],'hotel');
  const currency=selectField('Currency',[['USD','USD · US dollar'],['BRL','BRL · Brazilian real'],['EUR','EUR · Euro'],['GBP','GBP · British pound'],['CAD','CAD · Canadian dollar'],['AUD','AUD · Australian dollar'],['NZD','NZD · New Zealand dollar'],['MXN','MXN · Mexican peso'],['CHF','CHF · Swiss franc']],'USD');
  const timezone=field('Time zone',{full:true,value:Intl.DateTimeFormat().resolvedOptions().timeZone||'UTC',hint:'Use an IANA time zone, such as America/Sao_Paulo or Europe/London.',attrs:{maxlength:100}});
  openModal('Add a property',formShell([name.wrapper,kind.wrapper,currency.wrapper,timezone.wrapper],async()=>{
    const item=await api('/api/properties',{name:name.input.value.trim(),kind:kind.input.value,currency:currency.input.value,timeZoneId:timezone.input.value.trim()});selectProperty(item.id);state.calendar=null;toast('Property created. Add a room type or rental unit next.');
  },'Create property','Start with the essentials. Your property’s currency applies to all its rate plans.'));
}
function roomModal(item) {
  const name=field(item.kind==='rental'?'Rental unit name':'Room type name',{full:true,attrs:{maxlength:120,placeholder:item.kind==='rental'?'e.g. Garden apartment':'e.g. Deluxe double'}});
  const quantity=field('Number of units',{type:'number',value:1,attrs:{min:1,max:item.kind==='rental'?1:1000,step:1,readonly:item.kind==='rental'?'readonly':null},hint:item.kind==='rental'?'Each vacation rental is a separate unit.':'Interchangeable rooms of this type.'});
  const occupancy=field('Maximum guests per unit',{type:'number',value:2,attrs:{min:1,max:30,step:1}});
  openModal('Add room or rental unit',formShell([name.wrapper,quantity.wrapper,occupancy.wrapper],async()=>{
    await api(`/api/properties/${item.id}/rooms`,{name:name.input.value.trim(),quantity:Number(quantity.input.value),occupancy:Number(occupancy.input.value)});selectProperty(item.id);toast('Room added. Create a rate plan to set its prices.');
  },'Add room or unit','New units start with open inventory equal to their total capacity. Prices are defined by rate plans.'));
}
function rateModal(room) {
  const item=state.properties.find(item=>item.roomTypes?.some(value=>value.id===room.id));
  const name=field('Rate plan name',{full:true,attrs:{maxlength:120,placeholder:'e.g. Flexible rate'}});
  const price=field(`Base nightly price (${item?.currency||'USD'})`,{type:'number',full:true,attrs:{min:'0.01',max:1000000,step:'0.01'},hint:'Used on dates without a saved calendar price. Override prices by date in the calendar.'});
  openModal('Add a rate plan',formShell([name.wrapper,price.wrapper],async()=>{
    await api(`/api/rooms/${room.id}/rates`,{name:name.input.value.trim(),basePrice:Number(price.input.value)});if(item)selectProperty(item.id);toast('Rate plan added. Open this property’s calendar to edit its prices.');
  },'Create rate plan',`${room.name} · Create a sellable rate and its starting price.`));
}

function calendarModal(prefill={}) {
  const propertyId=state.selected,item=property(),roomList=rooms(),rateList=rates();let kind=prefill.kind||'rate';if(!rateList.length)kind='inventory';const drafts=[];
  const touched=new Set(),requireTouched=Boolean(prefill.day);let lastAddedEditorState='',reviewedRequest=null;
  const form=el('form');const error=el('div',{class:'alert error',role:'alert',hidden:true});
  const typeButtons=el('div',{class:'segment'});const fieldArea=el('div');const draftList=el('div',{class:'draft-list',hidden:true});const count=el('span',{class:'draft-count'},'No changes added');
  const preview=el('section',{class:'calendar-save-preview',hidden:true,'aria-live':'polite'});
  const save=el('button',{class:'button primary',type:'button'},icon('check'),'Review all changes');
  const confirm=el('button',{class:'button primary',type:'button',hidden:true},icon('check'),'Confirm and save changes');
  const today=propertyToday();
  const from=field('From date',{type:'date',value:prefill.date||state.from,attrs:{min:today,max:addDays(today,730)}});const to=field('Through date',{type:'date',value:prefill.date||state.from,attrs:{min:today,max:addDays(today,730)}});
  const room=selectField('Room type / unit',roomList.map(room=>[room.id,room.name]),prefill.roomId||'',{full:true,placeholder:'Choose a room type / unit'});
  const rate=selectField('Rate plan',rateList.map(rate=>[rate.id,`${rate.roomName} · ${rate.name}`]),prefill.rateId||'',{full:true,placeholder:'Choose a room and rate plan'});
  const capacity=field('Open inventory',{type:'number',value:prefill.capacity===undefined?'':prefill.capacity,attrs:{min:0,max:roomList.find(item=>item.id===room.input.value)?.quantity||0,step:1},hint:'Inventory before active reservations are subtracted. Cannot be lower than booked units; use stop sell to close sales when guests are booked.'});
  const price=field(`Nightly price (${item.currency})`,{type:'number',required:false,value:prefill.day?.rate??'',attrs:{min:'0.01',max:1000000,step:'0.01',placeholder:'Unchanged'}});
  const minArrival=field('Minimum stay on arrival',{type:'number',required:false,value:prefill.day?.minStayArrival??'',attrs:{min:1,max:365,step:1,placeholder:'Unchanged'}});
  const minThrough=field('Minimum stay through',{type:'number',required:false,value:prefill.day?.minStayThrough??'',attrs:{min:1,max:365,step:1,placeholder:'Unchanged'}});
  const maxStay=field('Maximum stay',{type:'number',required:false,value:prefill.day?.maxStay??'',attrs:{min:0,max:365,step:1,placeholder:'Unchanged'},hint:'Zero means no maximum.'});
  const boolOptions=[['','Unchanged'],['false','Open / no'],['true','Closed / yes']];
  const stop=selectField('Stop sell',boolOptions,prefill.day?String(prefill.day.stopSell):'',{required:false});
  const cta=selectField('Closed to arrival',boolOptions,prefill.day?String(prefill.day.closedToArrival):'',{required:false});
  const ctd=selectField('Closed to departure',boolOptions,prefill.day?String(prefill.day.closedToDeparture):'',{required:false});
  const pairId=`same-min-${Math.random().toString(36).slice(2,10)}`,reapplyId=`reapply-${Math.random().toString(36).slice(2,10)}`;
  const sameMinimum=el('input',{id:pairId,type:'checkbox',checked:!prefill.day||prefill.day.minStayArrival===prefill.day.minStayThrough});
  const reapply=el('input',{id:reapplyId,type:'checkbox'});
  const sameMinimumOption=el('label',{class:'check-option',for:pairId},sameMinimum,el('span',{},'Use the same minimum stay for arrival and through'));
  const reapplyOption=el('label',{class:'check-option',for:reapplyId},reapply,el('span',{},'Reapply selected values to channels',el('small',{},'Queue these selected fields again even when their saved values already match. Use this for recovery.')));
  const numericFields=[['rate',price,'Nightly price'],['minStayArrival',minArrival,'Minimum stay on arrival'],['minStayThrough',minThrough,'Minimum stay through'],['maxStay',maxStay,'Maximum stay']];
  const booleanFields=[['stopSell',stop,'Stop sell'],['closedToArrival',cta,'Closed to arrival'],['closedToDeparture',ctd,'Closed to departure']];
  const selectedFields=[...numericFields,...booleanFields];
  const invalidateReview=()=>{reviewedRequest=null;preview.hidden=true;confirm.hidden=true;save.hidden=false;};
  const editState=()=>JSON.stringify({kind,from:from.input.value,to:to.input.value,room:room.input.value,rate:rate.input.value,capacity:capacity.input.value,fields:selectedFields.map(([name,entry])=>[name,entry.input.value,touched.has(name)]),sameMinimum:sameMinimum.checked});
  const markEdited=(name,entry)=>{
    touched.add(name);
    if(sameMinimum.checked&&(name==='minStayArrival'||name==='minStayThrough')){
      const other=name==='minStayArrival'?minThrough:minArrival;other.input.value=entry.input.value;touched.add('minStayArrival');touched.add('minStayThrough');
    }
    invalidateReview();
  };
  for(const [name,entry]of selectedFields){entry.input.addEventListener('input',()=>markEdited(name,entry));entry.input.addEventListener('change',()=>markEdited(name,entry));}
  const contextEdited=()=>{
    // A cell's untouched saved values belong to its original date/rate. Keep only selected edits when switching context.
    if(requireTouched)for(const [name,entry]of selectedFields)if(!touched.has(name))entry.input.value='';
    invalidateReview();
  };
  for(const entry of [room,rate,from,to]){entry.input.addEventListener('input',contextEdited);entry.input.addEventListener('change',contextEdited);}
  capacity.input.addEventListener('input',invalidateReview);capacity.input.addEventListener('change',invalidateReview);
  sameMinimum.addEventListener('change',()=>{if(sameMinimum.checked&&(touched.has('minStayArrival')||touched.has('minStayThrough'))){touched.add('minStayArrival');touched.add('minStayThrough');}invalidateReview();});
  reapply.addEventListener('change',invalidateReview);
  room.input.addEventListener('change',()=>{capacity.input.max=roomList.find(item=>item.id===room.input.value)?.quantity||0;});
  const drawFields=()=>{
    fieldArea.replaceChildren(el('div',{class:'form-grid'},kind==='inventory'?room.wrapper:rate.wrapper,from.wrapper,to.wrapper,kind==='inventory'?capacity.wrapper:null));
    if(kind==='rate')fieldArea.append(el('p',{class:'editor-subtitle'},requireTouched?'SAVED VALUES ARE SHOWN · ONLY FIELDS YOU EDIT WILL BE SENT':'CHANGE ONLY WHAT YOU NEED · BLANK FIELDS STAY UNCHANGED'),sameMinimumOption,el('div',{class:'form-grid'},price.wrapper,minArrival.wrapper,minThrough.wrapper,maxStay.wrapper,stop.wrapper,cta.wrapper,ctd.wrapper));
    for(const btn of typeButtons.children)btn.classList.toggle('active',btn.dataset.kind===kind);
  };
  const inventoryType=button('Open inventory',()=>{kind='inventory';invalidateReview();drawFields();});inventoryType.dataset.kind='inventory';
  const rateType=button('Rates & restrictions',()=>{kind='rate';invalidateReview();drawFields();});rateType.dataset.kind='rate';rateType.disabled=!rateList.length;typeButtons.append(rateType,inventoryType);drawFields();
  const renderDrafts=()=>{
    draftList.replaceChildren();draftList.hidden=!drafts.length;count.textContent=`${drafts.length} ${drafts.length===1?'change':'changes'} ready to save`;
    drafts.forEach((draft,index)=>draftList.append(el('div',{class:'draft-row'},el('div',{},el('strong',{},draft.label),el('p',{},`${draft.change.dateFrom} → ${draft.change.dateTo} · ${draft.summary}`)),button('Remove',()=>{drafts.splice(index,1);invalidateReview();renderDrafts();},{small:true,subtle:true}))));
  };
  const addDraft=()=>{
    error.hidden=true;
    if(!form.reportValidity())return false;
    if(drafts.length>=100)throw new Error('Save this batch before adding more changes. Each batch supports up to 100 changes.');
    if(to.input.value<from.input.value)throw new Error('The through date must be on or after the from date.');
    if((new Date(to.input.value)-new Date(from.input.value))/86400000>=500)throw new Error('Each date range can contain up to 500 days.');
    const change={dateFrom:from.input.value,dateTo:to.input.value};let label,summary=[];
    if(kind==='inventory') {
      if(capacity.input.value==='')throw new Error('Enter an open inventory value.');
      change.roomTypeId=room.input.value;change.capacity=Number(capacity.input.value);label=roomList.find(item=>item.id===room.input.value)?.name;summary.push(`Open inventory ${change.capacity} before reservations`);
    }else{
      change.ratePlanId=rate.input.value;const selected=rateList.find(item=>item.id===rate.input.value);if(!selected)throw new Error('Choose a rate plan from this property.');label=`${selected.roomName} · ${selected.name}`;
      const include=(name,entry)=>entry.input.value!==''&&(!requireTouched||touched.has(name));
      const useMinimum=sameMinimum.checked&&(include('minStayArrival',minArrival)||include('minStayThrough',minThrough));
      if(useMinimum){
        if(minArrival.input.value!==''&&minThrough.input.value!==''&&Number(minArrival.input.value)!==Number(minThrough.input.value))throw new Error('Arrival and through minimum stays must match while “Use the same minimum stay” is selected. Enter one value again to update both, or uncheck that option.');
        const minimum=Number(include('minStayArrival',minArrival)?minArrival.input.value:minThrough.input.value);change.minStayArrival=minimum;change.minStayThrough=minimum;summary.push(`Minimum stay on arrival ${minimum}`,`Minimum stay through ${minimum}`);
      }
      for(const [name,entry,description]of numericFields)if(!(useMinimum&&['minStayArrival','minStayThrough'].includes(name))&&include(name,entry)){change[name]=Number(entry.input.value);summary.push(`${description}: ${name==='rate'?money(change[name]):change[name]}`);}
      for(const [name,entry,description]of booleanFields)if(include(name,entry)){change[name]=entry.input.value==='true';summary.push(`${description}: ${change[name]?'Yes':'No'}`);}
      if(!summary.length)throw new Error(requireTouched?'Edit a price or restriction first. Untouched saved fields are not selected for sending.':'Enter a price or restriction to change.');
    }
    if(!drafts.some(draft=>JSON.stringify(draft.change)===JSON.stringify(change)))drafts.push({change,label,summary:summary.join(' · ')});
    else toast('This exact change is already in the batch.');
    lastAddedEditorState=editState();invalidateReview();renderDrafts();return true;
  };
  const showError=issue=>{error.textContent=issue.message;error.hidden=false;error.scrollIntoView({block:'nearest'});};
  const add=el('button',{class:'button',type:'submit'},icon('plus'),'Add change to batch');
  form.addEventListener('submit',event=>{event.preventDefault();try{addDraft();}catch(issue){showError(issue);}});
  save.addEventListener('click',async()=>{
    try{
      if(!drafts.length&&!addDraft())return;
      else if(editState()!==lastAddedEditorState)throw new Error('The current editor contains a change that is not in the batch. Click “Add change to batch” before reviewing so that it is included.');
      error.hidden=true;reviewedRequest=JSON.parse(JSON.stringify({changes:drafts.map(draft=>draft.change),reapply:reapply.checked}));
      const body=el('tbody');for(const draft of drafts)body.append(el('tr',{},el('td',{},draft.label),el('td',{},draft.change.dateFrom),el('td',{},draft.change.dateTo),el('td',{},draft.summary)));
      const kinds=new Set(drafts.map(draft=>draft.change.roomTypeId?'Availability':'Rates & restrictions'));
      preview.replaceChildren(el('h3',{},'Review the exact changes'),el('p',{},el('strong',{},item.name),` · ${item.currency}`),el('div',{class:'table-scroll'},el('table',{class:'data-table change-summary-table'},el('thead',{},el('tr',{},['ROOM / RATE PLAN','FROM','THROUGH (INCLUSIVE)','SELECTED VALUES'].map(title=>el('th',{scope:'col'},title)))),body)),el('p',{class:'form-note'},`${kinds.size} selected ${kinds.size===1?'batch':'batches'}: ${[...kinds].join(' + ')}. The worker groups each kind into a request; existing pending work and retries may also be sent.`),reapply.checked?el('p',{class:'alert info'},'Reapply is enabled. The selected fields will be queued even when their saved values already match.'):null);
      preview.hidden=false;save.hidden=true;confirm.hidden=false;preview.scrollIntoView({block:'nearest'});
    }catch(issue){showError(issue);}
  });
  confirm.addEventListener('click',async()=>{
    if(!reviewedRequest)return;
    try{await busy(confirm,async()=>{const controls=[...form.querySelectorAll('input,select,button')].map(node=>[node,node.disabled]);for(const [node]of controls)node.disabled=true;try{const result=await api(`/api/properties/${propertyId}/calendar`,reviewedRequest);closeModal();toast(result.savedChanges?`${result.savedChanges} calendar date updates saved and queued.`:'The saved values already match. No new changes were queued. Use Reapply to intentionally resend selected fields.');await refreshView();}finally{for(const [node,wasDisabled]of controls)node.disabled=wasDisabled;}});}catch(issue){invalidateReview();showError(issue);}
  });
  form.append(el('p',{class:'form-note'},'Select one date range, or add several changes to a batch. Review the property, complete room/rate name, inclusive dates and selected fields before saving. Every editor change must be added to the batch.'),error,typeButtons,fieldArea,el('div',{class:'form-actions'},add),draftList,reapplyOption,preview,el('div',{class:'form-actions'},count,button('Cancel',closeModal),save,confirm));
  openModal('Edit availability & rates',form,true);
}

function reservationModal(existing) {
  const propertyId=state.selected,item=property(),stay=existing?.stays?.[0];const localRooms=rooms().filter(room=>room.ratePlans?.length);
  const guest=field('Guest name',{full:true,value:existing?.guestName||'',attrs:{maxlength:120}});
  const room=selectField('Room type / unit',localRooms.map(room=>[room.id,room.name]),stay?.roomTypeId||'',{placeholder:'Choose a room type / unit'});
  const selectedRoom=()=>localRooms.find(item=>item.id===room.input.value);
  const rate=selectField('Rate plan',(selectedRoom()?.ratePlans||[]).map(rate=>[rate.id,`${selectedRoom().name} · ${rate.name}`]),stay?.ratePlanId||'',{placeholder:'Choose a rate plan'});
  const quantity=field('Number of units',{type:'number',value:stay?.quantity||1,attrs:{min:1,max:selectedRoom()?.quantity||1,step:1}});
  room.input.addEventListener('change',()=>{rate.input.replaceChildren(...optionSelect((selectedRoom()?.ratePlans||[]).map(rate=>[rate.id,`${selectedRoom().name} · ${rate.name}`]),'','Choose a rate plan').children);rate.input.value='';quantity.input.max=selectedRoom()?.quantity||1;});
  const today=propertyToday();
  const arrival=field('Arrival',{type:'date',value:existing?.arrival||'',attrs:{min:today,max:addDays(today,730)}});
  const departure=field('Departure',{type:'date',value:existing?.departure||'',attrs:{min:addDays(today,1),max:addDays(today,731)}});
  const form=el('form'),error=el('div',{class:'alert error',role:'alert',hidden:true}),preview=el('section',{class:'calendar-save-preview',hidden:true,'aria-live':'polite'});let reviewedRequest=null;
  const review=button('Review reservation',()=>{
    try{
      error.hidden=true;if(!form.reportValidity())return;
      if(departure.input.value<=arrival.input.value)throw new Error('Departure must be after arrival.');
      const nights=(new Date(departure.input.value)-new Date(arrival.input.value))/86400000;
      if(nights>500)throw new Error('A reservation can contain up to 500 nights.');
      const selected=selectedRoom(),selectedRate=selected?.ratePlans?.find(plan=>plan.id===rate.input.value);
      if(!selected||!selectedRate)throw new Error('Choose a room and one of its rate plans.');
      reviewedRequest={guestName:guest.input.value.trim(),roomTypeId:room.input.value,ratePlanId:rate.input.value,quantity:Number(quantity.input.value),arrival:arrival.input.value,departure:departure.input.value};
      const body=el('tbody');for(const [label,value]of [['Property',`${item.name} · ${item.currency}`],['Guest',reviewedRequest.guestName],['Room / rate plan',`${selected.name} · ${selectedRate.name}`],['Arrival',reviewedRequest.arrival],['Departure (excluded)',reviewedRequest.departure],['Nights',nights],['Number of units',reviewedRequest.quantity],['Inventory affected',`${reviewedRequest.arrival} → ${addDays(reviewedRequest.departure,-1)} inclusive`]])body.append(el('tr',{},el('th',{scope:'row'},label),el('td',{},value)));
      preview.replaceChildren(el('h3',{},'Review this reservation'),el('div',{class:'table-scroll'},el('table',{class:'data-table change-summary-table'},body)),el('p',{class:'form-note'},'The departure date is checkout and does not consume inventory. Confirm the complete dates and room/rate plan before saving.'));preview.hidden=false;review.hidden=true;confirm.hidden=false;preview.scrollIntoView({block:'nearest'});
    }catch(issue){showError(issue);}
  },{primary:true,icon:'check'});
  const confirm=button(existing?'Confirm reservation changes':'Confirm and create reservation',async()=>{
    if(!reviewedRequest)return;
    try{await busy(confirm,async()=>{const controls=[...form.querySelectorAll('input,select,button')].map(node=>[node,node.disabled]);for(const [node]of controls)node.disabled=true;try{await api(existing?`/api/reservations/${existing.id}`:`/api/properties/${propertyId}/reservations`,reviewedRequest,existing?'PUT':'POST');closeModal();toast(existing?'Reservation updated. Availability is recalculated automatically.':'Reservation saved. Availability is recalculated automatically.');await refreshView();}finally{for(const [node,wasDisabled]of controls)node.disabled=wasDisabled;}});}catch(issue){invalidateReview();showError(issue);}
  },{primary:true,icon:'check'});confirm.hidden=true;
  const invalidateReview=()=>{reviewedRequest=null;preview.hidden=true;confirm.hidden=true;review.hidden=false;};
  const showError=issue=>{error.textContent=issue.message;error.hidden=false;error.scrollIntoView({block:'nearest'});};
  for(const entry of [guest,room,rate,arrival,departure,quantity]){entry.input.addEventListener('input',invalidateReview);entry.input.addEventListener('change',invalidateReview);}
  form.addEventListener('submit',event=>{event.preventDefault();review.click();});
  const cancel=existing?button('Cancel reservation',()=>cancelReservationModal(existing),{danger:true,small:true}):null;if(cancel)cancel.classList.add('left');
  form.append(el('p',{class:'form-note'},'Choose the room, rate plan and full arrival/departure dates explicitly. Direct reservations reduce available inventory for every occupied night.'),error,el('div',{class:'form-grid'},guest.wrapper,room.wrapper,rate.wrapper,arrival.wrapper,departure.wrapper,quantity.wrapper),preview,el('div',{class:'form-actions'},cancel,button('Cancel',closeModal),review,confirm));
  openModal(existing?'Manage reservation':'New direct reservation',form,true);
}
function confirmationModal(title,message,onConfirm,label='Confirm',danger=false) {
  const error=el('div',{class:'alert error',hidden:true,role:'alert'});const submit=button(label,async event=>{
    error.hidden=true;try{await busy(event.currentTarget,async()=>{await onConfirm();closeModal();await refreshView();});}catch(issue){error.textContent=issue.message;error.hidden=false;}
  },{primary:!danger,danger});
  openModal(title,el('div',{},el('p',{class:'form-note'},message),error,el('div',{class:'form-actions'},button('Go back',closeModal),submit)));
}
function cancelReservationModal(item) { confirmationModal('Cancel this reservation?',`Cancel ${item.guestName}’s reservation from ${dateLabel(item.arrival)} to ${dateLabel(item.departure)}. Its booked units will return to availability and the change will enter your sync queue.`,async()=>{await api(`/api/reservations/${item.id}`,undefined,'DELETE');toast('Reservation cancelled and availability updated.');},'Cancel reservation',true); }
function fullSyncModal() { confirmationModal('Queue a full calendar sync?',`Send 500 days of saved availability, prices and restrictions for ${property().name}. Use this after initial mapping or to recover the remote calendar. Regular operational changes already sync automatically.`,async()=>{await api(`/api/properties/${state.selected}/full-sync`,{});toast('Full sync queued. Follow its progress in sync history.');},'Queue full sync'); }
function clearMappingModal() { confirmationModal('Clear this property’s mapping?','Remove the links to Channex for this property, its rooms and rate plans. Local property data remains saved. Updates cannot be sent until the property is mapped again.',async()=>{await api(`/api/properties/${state.selected}/mapping`,{channexId:null,rooms:rooms().map(room=>({id:room.id,channexId:null})),rates:rates().map(rate=>({id:rate.id,channexId:null}))},'PUT');toast('Local Channex mapping cleared.');},'Clear mapping',true); }

function navigate(view) {
  if(!['calendar','reservations','properties','connection'].includes(view))view='calendar';
  state.view=view;if(location.hash!==`#${view}`)history.replaceState(null,'',`#${view}`);updateChrome();return refreshView(false);
}
for(const nav of document.querySelectorAll('[data-view]'))nav.addEventListener('click',()=>navigate(nav.dataset.view));
$('.brand').addEventListener('click',event=>{event.preventDefault();navigate('calendar');});
$('#property-select').addEventListener('change',()=>{selectProperty($('#property-select').value);refreshView(false);});
$('#refresh-button').addEventListener('click',()=>refreshView());
$('#modal-close').addEventListener('click',closeModal);
$('#modal').addEventListener('click',event=>{if(event.target===$('#modal')){const bounds=$('#modal').getBoundingClientRect();if(event.clientX<bounds.left||event.clientX>bounds.right||event.clientY<bounds.top||event.clientY>bounds.bottom)closeModal();}});
window.addEventListener('hashchange',()=>navigate(location.hash.slice(1)));
state.view=['calendar','reservations','properties','connection'].includes(location.hash.slice(1))?location.hash.slice(1):'calendar';
await refreshView();
