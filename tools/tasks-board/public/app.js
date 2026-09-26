// Task board client: a read-only view over the queue snapshot, refreshed on a
// timer. The board never writes — the CLI owns transitions — so there is
// nothing to submit from here, only to look at.

const OPEN_STATUSES = ['ready', 'claimed', 'review', 'blocked'];
const POLL_MS = 5000;

const state = {
  tasks: [],
  events: [],
  stats: {},
  db: '',
  status: 'open',
  query: '',
  selected: null,
  busy: false,
};

const $ = (sel) => document.querySelector(sel);
const el = {
  filters: $('#filters'),
  search: $('#search'),
  stats: $('#stats'),
  rows: $('#rows'),
  empty: $('#empty'),
  connDot: $('#conn-dot'),
  connText: $('#conn-text'),
  dbPath: $('#db-path'),
  sheet: $('#sheet'),
  sheetId: $('#sheet-id'),
  sheetBody: $('#sheet-body'),
  sheetClose: $('#sheet-close'),
  sheetInvestigate: $('#sheet-investigate'),
  investigate: $('#investigate'),
  investigateCount: $('#investigate-count'),
  toasts: $('#toasts'),
};

// A task is worth investigating when it is still claimed by a lease that has
// lapsed: the agent that took it stopped without submitting anything.
const isLapsed = (task) => task.status === 'claimed' && task.lease_expired;

// ---------------------------------------------------------------------------
// helpers

const text = (value) => document.createTextNode(String(value ?? ''));

function node(tag, className, child) {
  const n = document.createElement(tag);
  if (className) n.className = className;
  if (child !== undefined) n.append(child);
  return n;
}

function ago(iso) {
  if (!iso) return '—';
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return iso;
  const secs = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (secs < 60) return `${secs}s ago`;
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 48) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

function statusBadge(task) {
  if (task.status === 'claimed' && task.lease_expired) {
    return node('span', 'badge expired', text('expired'));
  }
  return node('span', `badge ${task.status}`, text(task.status));
}

function matches(task) {
  const { status, query } = state;
  if (status === 'open' && !OPEN_STATUSES.includes(task.status)) return false;
  else if (status !== 'open' && status !== 'all' && task.status !== status) return false;

  if (!query) return true;
  const hay = [task.id, task.title, task.area, task.agent, task.branch, task.worktree]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
  return hay.includes(query);
}

// ---------------------------------------------------------------------------
// rendering

function renderStats() {
  const s = state.stats;
  // Count leases on the client rather than trusting stats.expired_leases: the
  // server flags any task whose lease_until has passed, including finished
  // ones, which would report every completed task as an expired lease.
  const expired = state.tasks.filter((t) => t.status === 'claimed' && t.lease_expired).length;
  const cells = [
    ['open', s.open ?? 0, ''],
    ['actionable', s.actionable_ready ?? 0, 'good'],
    ['waiting deps', s.waiting_on_deps ?? 0, s.waiting_on_deps ? 'alert' : ''],
    ['claimed', s.claimed ?? 0, ''],
    ['review', s.review ?? 0, ''],
    ['done', s.done ?? 0, 'good'],
    ['expired', expired, expired ? 'alert' : ''],
    ['total', s.total ?? 0, ''],
  ];
  el.stats.replaceChildren(
    ...cells.map(([label, value, tone]) => {
      const box = node('div', `stat ${tone}`.trim());
      box.append(node('b', '', text(value)), node('span', '', text(label)));
      return box;
    })
  );
}

// Priority first (1 is the most urgent, matching the CLI's own `priority, seq`
// ordering), then most recently touched inside each priority band: the board
// should lead with what matters, not merely with what moved. Ties fall back to
// seq so the order is stable across polls.
function byPriorityThenUpdated(a, b) {
  if (a.priority !== b.priority) return a.priority - b.priority;
  const diff = (Date.parse(b.updated) || 0) - (Date.parse(a.updated) || 0);
  return diff !== 0 ? diff : b.seq - a.seq;
}

function renderRows() {
  const visible = state.tasks.filter(matches).sort(byPriorityThenUpdated);
  const rows = visible.map((task) => {
    const tr = node('tr');
    tr.append(
      node('td', 'col-id mono', text(task.id)),
      node('td', `col-pri pri p${task.priority}`, text(task.priority)),
      node('td', 'col-status', statusBadge(task))
    );

    const title = node('td');
    title.append(node('span', 'title', text(task.title)));
    if (task.unmet_deps.length || task.blocked_on) {
      const why = [];
      if (task.blocked_on) why.push(`blocked on ${task.blocked_on}`);
      if (task.unmet_deps.length) why.push(`waiting on ${task.unmet_deps.join(', ')}`);
      title.append(node('span', 'deps', text(why.join(' · '))));
    }
    tr.append(title);

    let agentText = task.agent || '—';
    if (task.status === 'claimed' && task.lease_expired) agentText += ' (lease expired)';
    tr.append(
      node('td', 'col-area small muted', text(task.area || '—')),
      node('td', 'col-agent small muted', text(agentText)),
      node('td', 'col-when small muted', text(ago(task.updated)))
    );
    tr.addEventListener('click', () => openSheet(task.id));
    return tr;
  });

  el.rows.replaceChildren(...rows);
  el.empty.hidden = rows.length > 0;
}

function renderFooter() {
  el.dbPath.textContent = state.db;
  el.dbPath.title = state.db;
}

function renderInvestigate() {
  const lapsed = state.tasks.filter(isLapsed);
  el.investigateCount.textContent = lapsed.length ? String(lapsed.length) : '';
  el.investigate.disabled = lapsed.length === 0 || state.busy;
  el.investigate.title = lapsed.length
    ? `Ask a Paseo agent to triage ${lapsed.map((t) => t.id).join(', ')}`
    : 'No lapsed claims to investigate';

  const selected = state.tasks.find((t) => t.id === state.selected);
  el.sheetInvestigate.disabled = !selected || !isLapsed(selected) || state.busy;
}

function render() {
  renderStats();
  renderRows();
  renderFooter();
  renderInvestigate();
  if (state.selected) refreshSheet();
}

// ---------------------------------------------------------------------------
// investigate

function toast(tone, title, body) {
  const box = document.createElement('div');
  box.className = `toast ${tone}`;
  const head = document.createElement('div');
  head.className = 'toast-title';
  head.textContent = title;
  box.append(head);
  if (body) {
    const text = document.createElement('div');
    text.className = 'toast-body';
    text.innerHTML = body;
    box.append(text);
  }
  el.toasts.append(box);
  setTimeout(() => box.remove(), tone === 'err' ? 12_000 : 30_000);
}

async function investigate(ids) {
  if (state.busy || ids.length === 0) return;
  state.busy = true;
  renderInvestigate();
  el.sheetInvestigate.disabled = true;
  try {
    // The custom header is what proves this is the board's own page talking.
    const res = await fetch('/api/investigate', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-taskboard': '1' },
      body: JSON.stringify({ ids }),
    });
    const body = await res.json();
    if (!res.ok) throw new Error(body.error || `request failed (${res.status})`);
    toast(
      'ok',
      `Investigating ${body.tasks.join(', ')}`,
      `Agent <code>${body.shortId}</code> is triaging in the background. ` +
        `Follow it with <code>paseo logs ${body.shortId}</code>.`
    );
  } catch (err) {
    toast('err', 'Could not start the agent', String(err.message ?? err));
  } finally {
    state.busy = false;
    renderInvestigate();
  }
}

// ---------------------------------------------------------------------------
// detail sheet

function field(dl, label, value) {
  if (!value) return;
  dl.append(node('dt', '', text(label)), node('dd', '', text(value)));
}

function openSheet(id) {
  state.selected = id;
  refreshSheet();
  if (!el.sheet.open) el.sheet.showModal();
}

function refreshSheet() {
  const task = state.tasks.find((t) => t.id === state.selected);
  if (!task) {
    el.sheet.close();
    state.selected = null;
    return;
  }
  el.sheetId.textContent = `${task.id} — ${task.title}`;

  const body = [];
  const dl = node('dl');
  field(dl, 'status', task.status);
  field(dl, 'area', task.area);
  field(dl, 'priority', String(task.priority));
  field(dl, 'agent', task.agent);
  field(dl, 'branch', task.branch);
  field(dl, 'worktree', task.worktree);
  field(dl, 'commit', task.commit_sha);
  field(dl, 'pr', task.pr ? `#${task.pr}` : '');
  field(dl, 'blocked on', task.blocked_on);
  field(dl, 'lease until', task.lease_until);
  field(dl, 'created', task.created);
  field(dl, 'updated', task.updated);
  field(dl, 'finished', task.finished_at);
  body.push(dl);

  if (task.note) {
    body.push(node('h4', '', text('note')), node('p', 'pre mono', text(task.note)));
  }

  if (task.deps.length) {
    const list = node('ul');
    for (const dep of task.deps) {
      const suffix = task.unmet_deps.includes(dep.id) ? ' — unmet' : '';
      list.append(node('li', 'mono', text(`${dep.id} ${dep.title} (${dep.status})${suffix}`)));
    }
    body.push(node('h4', '', text('dependencies')), list);
  }

  const history = state.events.filter((e) => e.task_id === task.id);
  if (history.length) {
    const list = node('ul', 'events');
    for (const e of history) {
      const li = node('li', 'small');
      li.append(
        text(`${ago(e.at)} `),
        node('span', 'arrow', text(e.from_status ? `${e.from_status} → ${e.to_status}` : e.to_status))
      );
      const who = e.actor || 'queue';
      li.append(text(` · ${who}${e.detail ? ` · ${e.detail}` : ''}`));
      list.append(li);
    }
    body.push(node('h4', '', text('history')), list);
  }

  el.sheetBody.replaceChildren(...body);
}

// ---------------------------------------------------------------------------
// live connection

function setConnection(stateName, label) {
  el.connDot.className = `dot ${stateName}`;
  el.connText.textContent = label;
}

// Each poll returns a whole snapshot, so a render is a straight replacement —
// there is no incremental state to merge and no cursor to resume from.
function apply(snapshot) {
  state.tasks = snapshot.tasks ?? [];
  state.stats = snapshot.stats ?? {};
  state.events = snapshot.events ?? [];
  if (snapshot.db) state.db = snapshot.db;
  render();
}

// ---------------------------------------------------------------------------
// wiring

el.filters.addEventListener('click', (e) => {
  const button = e.target.closest('.filter');
  if (!button) return;
  for (const b of el.filters.querySelectorAll('.filter')) b.classList.toggle('active', b === button);
  state.status = button.dataset.status;
  renderRows();
});

el.search.addEventListener('input', () => {
  state.query = el.search.value.trim().toLowerCase();
  renderRows();
});

el.sheetClose.addEventListener('click', () => {
  el.sheet.close();
  state.selected = null;
});

el.investigate.addEventListener('click', () => {
  investigate(state.tasks.filter(isLapsed).map((t) => t.id));
});

el.sheetInvestigate.addEventListener('click', () => {
  const selected = state.tasks.find((t) => t.id === state.selected);
  if (selected) investigate([selected.id]);
});

el.sheet.addEventListener('close', () => {
  state.selected = null;
});

document.addEventListener('keydown', (e) => {
  if (e.key === '/' && document.activeElement !== el.search) {
    e.preventDefault();
    el.search.focus();
  }
});

// Refresh on a timer. Overlapping polls are skipped, a hidden tab stops
// polling until it is shown again, and the indicator reports the last poll.
let polling = false;
async function poll() {
  if (polling || document.hidden) return;
  polling = true;
  try {
    const res = await fetch('/api/snapshot', { cache: 'no-store' });
    if (!res.ok) throw new Error(`snapshot: ${res.status}`);
    apply(await res.json());
    setConnection('ok', 'live');
  } catch {
    setConnection('danger', 'offline');
  } finally {
    polling = false;
  }
}

setInterval(poll, POLL_MS);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) poll();
});

poll();
