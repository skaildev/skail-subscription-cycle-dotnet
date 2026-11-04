const KEY = 'subscription-cycle.customerId';

const $ = (id) => document.getElementById(id);
const els = {
  name: $('name'), plan: $('plan'), trial: $('trial'), month: $('month'), card: $('card'), start: $('start'),
  subscribe: $('subscribe'), cancel: $('cancel'), state: $('state'), countdown: $('countdown'), error: $('error'),
  result: $('result'), timeline: $('timeline'), customerId: $('customerId'), monitor: $('monitor'),
  planView: $('planView'), monthView: $('monthView'), totalView: $('totalView'),
};
const FORM = [els.name, els.plan, els.trial, els.month, els.card];

let customer = null;
let source = null;
let busy = false;

const TERMINAL = ['Ended', 'Cancelled', 'Suspended', 'Expired', 'Failed'];
const isTerminal = (c) => c && TERMINAL.includes(c.phase);
const time = (iso) => new Date(iso).toLocaleTimeString();
const money = (v) => `R$ ${Number(v).toFixed(2)}`;

function showError(message) {
  els.error.hidden = !message;
  els.error.textContent = message ?? '';
}

function stateLabel(c) {
  if (!c) return 'Idle';
  switch (c.phase) {
    case 'Starting': return 'Starting…';
    case 'Trial': return 'Trial';
    case 'Subscribed': return c.cancelRequested ? 'Cancelling…' : 'Subscribed';
    case 'Declined': return 'Payment declined, retrying';
    default: return c.phase;
  }
}

function badgeClass(c) {
  if (!c) return 'idle';
  if (['Suspended', 'Failed'].includes(c.phase)) return 'failed';
  if (['Ended', 'Cancelled', 'Expired'].includes(c.phase)) return 'done';
  return c.phase === 'Declined' ? 'warn' : 'active';
}

function resultText(r) {
  const paid = `${r.monthsPaid} month(s) paid, ${money(r.totalCharged)}`;
  switch (r.end) {
    case 'Ended': return 'Trial ended without a subscription · nothing was charged';
    case 'Cancelled': return `Cancelled · ${paid} · no further charge`;
    case 'Suspended': return `Suspended at month ${r.month}: ${r.reason} · ${paid}`;
    default: return `Reached the month limit · ${paid}`;
  }
}

// the countdown is only visual: the real wait is skail's durable Delay
function tick() {
  const trial = customer?.phase === 'Trial' && customer.trialStartedAt;
  els.countdown.hidden = !trial;
  if (!trial) return;
  const left = Math.max(0, customer.trialSeconds - (Date.now() - new Date(customer.trialStartedAt).getTime()) / 1000);
  els.countdown.textContent = `trial ends in ${Math.ceil(left)}s`;
}

function render() {
  const active = customer?.active === true;
  for (const control of FORM) control.disabled = active || busy;
  els.start.disabled = active || busy;
  if (customer) {
    els.name.value = customer.name;
    els.plan.value = customer.plan;
    els.trial.value = customer.trialSeconds;
    els.month.value = customer.monthSeconds;
    els.card.value = customer.card;
  }

  // each action only while it still applies; they come back if the fire fails
  els.subscribe.disabled = busy || !(customer?.phase === 'Trial' && !customer.subscribeRequested);
  els.cancel.disabled = busy || !(['Subscribed', 'Declined'].includes(customer?.phase) && !customer.cancelRequested);

  els.state.textContent = stateLabel(customer);
  els.state.className = `badge ${badgeClass(customer)}`;
  els.customerId.textContent = customer?.customerId ?? '-';
  els.planView.textContent = customer?.plan ?? '-';
  els.monthView.textContent = customer?.month ? customer.month : '-';
  els.totalView.textContent = customer ? money(customer.totalCharged) : '-';
  showError(customer?.error);

  const r = customer?.result;
  els.result.hidden = !r;
  els.result.className = `result ${r && ['Suspended', 'Failed'].includes(customer.phase) ? 'bad' : 'ok'}`;
  if (r) els.result.textContent = resultText(r);

  els.timeline.replaceChildren(...(customer?.timeline ?? []).map((t) => {
    const item = document.createElement('li');
    item.className = t.kind;
    item.textContent = `${time(t.at)}  ${t.text}`;
    return item;
  }));
  tick();
}

function connect(customerId) {
  source?.close();
  source = new EventSource(`/api/customers/${customerId}/stream`);
  source.onmessage = (event) => {
    customer = JSON.parse(event.data);
    render();
    if (isTerminal(customer)) {
      source.close();
      localStorage.removeItem(KEY);
    }
  };
  // EventSource reconnects by itself; the stream sends the current snapshot first
}

async function post(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? `HTTP ${res.status}`);
  return res.json();
}

async function act(label, work) {
  busy = true;
  showError(null);
  render();
  try {
    await work();
  } catch (e) {
    showError(`${label}: ${e.message}`); // the server released the action: the button comes back
  }
  busy = false;
  render();
}

els.start.addEventListener('click', () => act('Could not start', async () => {
  customer = await post('/api/customers', {
    name: els.name.value, plan: els.plan.value, card: els.card.value,
    trialSeconds: Number(els.trial.value), monthSeconds: Number(els.month.value),
  });
  localStorage.setItem(KEY, customer.customerId);
  connect(customer.customerId);
}));

els.subscribe.addEventListener('click', () => act('Could not subscribe', () =>
  post(`/api/customers/${customer.customerId}/subscribe`, { plan: els.plan.value })));

els.cancel.addEventListener('click', () => act('Could not cancel', () =>
  post(`/api/customers/${customer.customerId}/cancel`, {})));

fetch('/api/config').then((r) => r.json()).then((c) => {
  els.name.placeholder = c.defaultName;
  if (!customer) { els.trial.value = c.defaultTrialSeconds; els.month.value = c.defaultMonthSeconds; }
  if (c.monitorUrl) { els.monitor.href = c.monitorUrl; els.monitor.hidden = false; }
}).catch(() => {});

// reload keeps the state right: it always comes from the server
const saved = localStorage.getItem(KEY);
if (saved) {
  fetch(`/api/customers/${saved}`).then((r) => (r.ok ? r.json() : null)).then((c) => {
    if (!c) return localStorage.removeItem(KEY);
    customer = c;
    render();
    if (!isTerminal(c)) connect(c.customerId);
  });
}
setInterval(tick, 500);
render();
