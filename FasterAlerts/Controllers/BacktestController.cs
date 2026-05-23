using System.Text;
using System.Text.Json;
using FasterAlerts.Data;
using FasterAlerts.Models;
using FasterAlerts.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FasterAlerts.Controllers;

[ApiController]
[Route("backtest")]
public class BacktestController(
    AppDbContext db,
    HeliusBacktestService helius) : ControllerBase
{
    // ── filter helper ─────────────────────────────────────────────────────

    private IQueryable<SentAlert> ApplyFilters(
        decimal minMcap, decimal maxMcap,
        decimal minLockPct,
        int minAgeDays, int maxAgeDays)
    {
        var q = db.SentAlerts.AsQueryable();
        if (minMcap > 0)     q = q.Where(a => a.MarketCapUsd >= minMcap);
        if (maxMcap > 0)     q = q.Where(a => a.MarketCapUsd <= maxMcap);
        if (minLockPct > 0)  q = q.Where(a => a.PercentSupply >= minLockPct);

        // age filter only when PairCreatedAt is known
        if (minAgeDays > 0 || maxAgeDays < 9999)
        {
            q = q.Where(a => a.PairCreatedAt != null);
            // SQLite can't compute TimeSpan inline — we'll filter in memory below
        }

        return q;
    }

    // ── GET /backtest (HTML page) ─────────────────────────────────────────

    [HttpGet]
    public ContentResult GetPage()
    {
        var html = new StringBuilder();
        html.Append("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>FASTER Locks — Backtest</title>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    background:#0e1621; color:#e8e9ea; min-height:100vh; padding:24px 16px; }
  .page-header { max-width:1400px; margin:0 auto 20px; display:flex; align-items:baseline; gap:14px; }
  .page-title  { font-size:17px; font-weight:600; }
  .page-sub    { font-size:12px; color:#6c7883; }
  .layout { max-width:1400px; margin:0 auto; display:grid;
    grid-template-columns:320px 1fr; gap:16px; align-items:start; }
  .card { background:#17212b; border-radius:10px; padding:16px; margin-bottom:14px; }
  .card-title { font-size:11px; font-weight:600; color:#6c7883;
    text-transform:uppercase; letter-spacing:.06em; margin-bottom:12px; }
  label { display:block; font-size:11px; color:#8a96a0; margin:10px 0 4px; }
  input[type=number], input[type=text] {
    background:#0e1621; border:1px solid #2b3a4a; border-radius:6px;
    color:#e8e9ea; font-size:13px; padding:6px 10px; width:100%; outline:none; }
  input:focus { border-color:#5ca8e2; }
  .btn { display:block; width:100%; margin-top:10px; background:#2b5278;
    border:none; border-radius:8px; color:#e8e9ea; font-size:13px; font-weight:600;
    padding:9px 0; cursor:pointer; transition:background .15s; }
  .btn:hover:not(:disabled) { background:#3a6fa0; }
  .btn:disabled { opacity:.45; cursor:default; }
  .btn-green { background:#1a4a30; }
  .btn-green:hover:not(:disabled) { background:#256040; }
  .btn-row { display:flex; gap:8px; }
  .btn-row .btn { margin-top:0; }

  /* cached list */
  .list-header { display:flex; justify-content:space-between; align-items:center; margin-bottom:8px; }
  .list-count  { font-size:11px; color:#6c7883; }
  .sel-link    { font-size:11px; color:#5ca8e2; cursor:pointer; background:none; border:none; padding:0; }
  .sel-link:hover { text-decoration:underline; }
  .search-box  { margin-bottom:8px; }
  .cache-list  { max-height:380px; overflow-y:auto; border:1px solid #2b3a4a; border-radius:8px; }
  .cache-item  { display:flex; align-items:center; gap:10px; padding:7px 10px;
    border-bottom:1px solid #1a2635; cursor:pointer; transition:background .1s; }
  .cache-item:last-child { border-bottom:none; }
  .cache-item:hover { background:rgba(92,168,226,.06); }
  .cache-item input[type=checkbox] { accent-color:#5ca8e2; width:14px; height:14px; cursor:pointer; flex-shrink:0; }
  .ci-sym  { font-weight:600; font-size:13px; min-width:60px; }
  .ci-mc   { font-size:11px; color:#6c7883; }
  .ci-lock { font-size:11px; color:#8a96a0; margin-left:auto; }
  .sel-badge { font-size:11px; font-weight:600; color:#5ca8e2; }

  /* stats */
  .stats-row { display:grid; grid-template-columns:repeat(3,1fr); gap:10px; margin-bottom:14px; }
  .stat-card { background:#17212b; border-radius:10px; padding:14px 16px; text-align:center; }
  .stat-val  { font-size:22px; font-weight:700; color:#5ca8e2; }
  .stat-lbl  { font-size:11px; color:#6c7883; margin-top:4px; }

  /* table */
  .tbl-wrap { background:#17212b; border-radius:10px; overflow:hidden; }
  table { width:100%; border-collapse:collapse; font-size:12px; }
  th { background:#0e1621; color:#6c7883; font-size:10px; font-weight:600;
    text-transform:uppercase; letter-spacing:.06em; padding:9px 12px; text-align:left; }
  td { padding:8px 12px; border-bottom:1px solid #0e1621; }
  tr:last-child td { border-bottom:none; }
  tr:hover td { background:rgba(92,168,226,.04); }
  .badge { display:inline-block; padding:2px 7px; border-radius:4px; font-size:11px; font-weight:600; }
  .badge-tp      { background:#1a4a30; color:#4caf50; }
  .badge-sl      { background:#4a1a1a; color:#e05656; }
  .badge-timeout { background:#2b3a4a; color:#8a96a0; }
  .no-data { padding:40px; text-align:center; color:#6c7883; font-size:13px; }
  .reco-row { display:flex; justify-content:space-between; align-items:center;
    padding:8px 0; border-bottom:1px solid #0e1621; font-size:13px; }
  .reco-row:last-child { border-bottom:none; }
  .reco-combo { font-weight:600; color:#5ca8e2; }
  .reco-ev    { color:#4caf50; font-size:12px; }
  .reco-pct   { color:#8a96a0; font-size:11px; }
</style>
</head>
<body>
<div class="page-header">
  <span class="page-title">🔬 Backtest Engine</span>
  <span class="page-sub">Helius price series · cached dataset · SL/TP simulation</span>
</div>

<div class="layout">
  <div>
    <!-- cached token picker -->
    <div class="card">
      <div class="list-header">
        <div class="card-title" style="margin-bottom:0">Cached Tokens</div>
        <span class="sel-badge" id="selCount">0 selected</span>
      </div>
      <div class="search-box">
        <input type="text" id="search" placeholder="Search symbol…" oninput="filterList()">
      </div>
      <div class="list-header">
        <span class="list-count" id="listCount">loading…</span>
        <span>
          <button class="sel-link" onclick="selectAll()">All</button>
          &nbsp;·&nbsp;
          <button class="sel-link" onclick="selectNone()">None</button>
        </span>
      </div>
      <div class="cache-list" id="cacheList"></div>
    </div>

    <!-- analysis controls -->
    <div class="card">
      <div class="card-title">Analysis</div>
      <label>Unit Size ($)</label>
      <input type="number" id="unitSize" value="40" min="1">
      <label>Stop Loss %</label>
      <input type="number" id="sl" value="25" min="1" max="99">
      <label>Take Profit %</label>
      <input type="number" id="tp" value="100" min="1">
      <div class="btn-row" style="margin-top:12px">
        <button class="btn btn-green" onclick="analyze()">Analyze</button>
        <button class="btn" onclick="recommend()">Best SL/TP</button>
      </div>
    </div>
  </div>

  <!-- main results -->
  <div>
    <div class="stats-row">
      <div class="stat-card"><div class="stat-val" id="sTp">—</div><div class="stat-lbl">TP Hit</div></div>
      <div class="stat-card"><div class="stat-val" id="sSl">—</div><div class="stat-lbl">SL Hit</div></div>
      <div class="stat-card"><div class="stat-val" id="sTo">—</div><div class="stat-lbl">Timeout / No data</div></div>
    </div>
    <div class="stats-row">
      <div class="stat-card"><div class="stat-val" id="sTpTime">—</div><div class="stat-lbl">Avg time to TP</div></div>
      <div class="stat-card"><div class="stat-val" id="sSlTime">—</div><div class="stat-lbl">Avg time to SL</div></div>
      <div class="stat-card"><div class="stat-val" id="sEv">—</div><div class="stat-lbl">EV / trade</div></div>
      </div>
      <div class="stats-row">
      <div class="stat-card" style="grid-column:span 2"><div class="stat-val" id="sTotalUsd">—</div><div class="stat-lbl">Total P&amp;L (all trades)</div></div>
      <div class="stat-card"><div class="stat-val" id="sEvUsd">—</div><div class="stat-lbl">EV / trade ($)</div></div>
    </div>
    <div class="stats-row">
      <div class="stat-card"><div class="stat-val" id="sPeak50">—</div><div class="stat-lbl">Peak gain p50</div></div>
      <div class="stat-card"><div class="stat-val" id="sPeak75">—</div><div class="stat-lbl">Peak gain p75</div></div>
      <div class="stat-card"><div class="stat-val" id="sPeak90">—</div><div class="stat-lbl">Peak gain p90</div></div>
    </div>
    <div class="tbl-wrap">
      <table>
        <thead>
          <tr>
            <th data-col="alertId" onclick="setSort('alertId')" style="cursor:pointer"># <span class="arr">↕</span></th>
            <th>Symbol / CA</th>
            <th data-col="alertTime" onclick="setSort('alertTime')" style="cursor:pointer">Notif At <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="notifMcap" onclick="setSort('notifMcap')" style="cursor:pointer">Entry MCap <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="exitMcap" onclick="setSort('exitMcap')" style="cursor:pointer">Exit MCap <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="lockPct" onclick="setSort('lockPct')" style="cursor:pointer">Lock% <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="outcome" onclick="setSort('outcome')" style="cursor:pointer">Outcome <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="exitPct" onclick="setSort('exitPct')" style="cursor:pointer">Exit % <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="minutes" onclick="setSort('minutes')" style="cursor:pointer">Time <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="peakGain" onclick="setSort('peakGain')" style="cursor:pointer">Peak Gain <span class="arr" style="opacity:.3">↕</span></th>
            <th data-col="maxDd" onclick="setSort('maxDd')" style="cursor:pointer">Max DD <span class="arr" style="opacity:.3">↕</span></th>
          </tr>
        </thead>
        <tbody id="resultsBody">
          <tr><td colspan="8" class="no-data">Select tokens and run analysis</td></tr>
        </tbody>
      </table>
    </div>
    <div class="card" id="recoCard" style="display:none;margin-top:14px">
      <div class="card-title">Best SL/TP Combinations</div>
      <div id="recoList"></div>
    </div>
  </div>
</div>

<script>
let allItems = [];

async function loadCached() {
  const r = await fetch('/backtest/api/cached');
  allItems = await r.json();
  renderList(allItems);
}

function filterList() {
  const q = document.getElementById('search').value.toLowerCase();
  renderList(allItems.filter(x => x.symbol.toLowerCase().includes(q)));
}

function renderList(items) {
  document.getElementById('listCount').textContent = `${items.length} cached`;
  const list = document.getElementById('cacheList');
  list.innerHTML = items.map(x => `
    <div class="cache-item" onclick="toggle(${x.id},this)">
      <input type="checkbox" id="chk_${x.id}" value="${x.id}" onchange="updateCount()">
      <span class="ci-sym">$${x.symbol}</span>
      <span class="ci-mc">${fmtMc(x.mcap)}</span>
      <span class="ci-lock">${x.lockPct.toFixed(1)}%</span>
    </div>`).join('');
  updateCount();
}

function toggle(id, row) {
  const chk = document.getElementById('chk_' + id);
  chk.checked = !chk.checked;
  updateCount();
}

function selectAll()  { document.querySelectorAll('.cache-list input').forEach(c=>c.checked=true);  updateCount(); }
function selectNone() { document.querySelectorAll('.cache-list input').forEach(c=>c.checked=false); updateCount(); }

function selectedIds() {
  return [...document.querySelectorAll('.cache-list input:checked')].map(c=>+c.value);
}

function updateCount() {
  const n = selectedIds().length;
  document.getElementById('selCount').textContent = `${n} selected`;
}

async function analyze() {
  const ids = selectedIds();
  if (!ids.length) return alert('Select at least one token');
  const r = await fetch('/backtest/api/analyze', {
    method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ sl: +document.getElementById('sl').value, tp: +document.getElementById('tp').value, unitSize: +document.getElementById('unitSize').value, ids })
  });
  renderResults(await r.json());
}

async function recommend() {
  const ids = selectedIds();
  if (!ids.length) return alert('Select at least one token');
  const r = await fetch('/backtest/api/recommend', {
    method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ unitSize: +document.getElementById('unitSize').value, ids })
  });
  renderRecommendations(await r.json());
}

// ── table state ───────────────────────────────────────────────────────────
let allRows = [], excluded = new Set(), sortCol = 'alertId', sortDir = 1;
let lastPeaks = {p50:0,p75:0,p90:0};

function renderResults(d) {
  lastPeaks = {p50:d.summary.peakP50, p75:d.summary.peakP75, p90:d.summary.peakP90};
  allRows = d.rows;
  excluded.clear();
  sortCol = 'alertId'; sortDir = 1;
  refreshTable();
  refreshSummary();
}

function getActive() {
  const rows = allRows.filter(r => !excluded.has(r.alertId));
  return [...rows].sort((a,b) => {
    const av=a[sortCol], bv=b[sortCol];
    return (typeof av==='string' ? av.localeCompare(bv) : av-bv) * sortDir;
  });
}

function setSort(col) {
  if (sortCol===col) sortDir=-sortDir; else {sortCol=col; sortDir=1;}
  refreshTable();
}

function excludeRow(id) { excluded.add(id); refreshTable(); refreshSummary(); }

function refreshTable() {
  document.querySelectorAll('th[data-col]').forEach(th => {
    const arr = th.querySelector('.arr');
    if (!arr) return;
    arr.textContent = th.dataset.col===sortCol ? (sortDir===1?'↑':'↓') : '↕';
    arr.style.opacity = th.dataset.col===sortCol ? '1' : '0.3';
  });
  const rows = getActive();
  const tbody = document.getElementById('resultsBody');
  if (!rows.length) { tbody.innerHTML='<tr><td colspan="12" class="no-data">No data</td></tr>'; return; }
  tbody.innerHTML = rows.map(r=>`
    <tr>
      <td style="color:#6c7883;white-space:nowrap">
        <span style="cursor:pointer;color:#e05656;margin-right:5px;font-size:11px" onclick="excludeRow(${r.alertId})">✕</span>#${r.alertId}
      </td>
      <td>
        <div style="font-weight:600">$${r.symbol}</div>
        <div style="font-size:10px;color:#5ca8e2;cursor:pointer;font-family:monospace" onclick="copyCA('${r.mint}',this)">${r.mint.slice(0,6)}…${r.mint.slice(-4)}</div>
      </td>
      <td style="font-size:11px;color:#8a96a0">${fmtTs(r.alertTime)}</td>
      <td style="font-size:12px">${fmtMc(r.notifMcap)} <span style="color:#6c7883">/</span> <span style="color:#5ca8e2">${fmtMc(r.entryMcap)}</span></td>
      <td>${r.outcome==='TIMEOUT'?'—':fmtMc(r.exitMcap)}</td>
      <td>${r.lockPct.toFixed(1)}%</td>
      <td><span class="badge badge-${r.outcome.toLowerCase()}">${r.outcome}</span></td>
      <td style="color:${r.exitPct>0?'#4caf50':r.exitPct<0?'#e05656':'#8a96a0'}">${r.outcome==='TIMEOUT'?'—':(r.exitPct>0?'+':'')+r.exitPct.toFixed(1)+'%'}</td>
      <td>${r.outcome==='TIMEOUT'?'—':fmtTime(r.minutes)}</td>
      <td style="color:${r.peakGain>0?'#4caf50':'#8a96a0'}">${r.peakGain>0?'+':''}${r.peakGain.toFixed(0)}%</td>
      <td style="color:#e05656">${r.maxDd.toFixed(0)}%</td>
    </tr>`).join('');
}

function refreshSummary() {
  const rows = getActive();
  const n = rows.length; if (!n) return;
  const tp = rows.filter(r=>r.outcome==='TP');
  const sl = rows.filter(r=>r.outcome==='SL');
  const to = rows.filter(r=>r.outcome==='TIMEOUT');
  const pnls = [...tp,...sl].map(r=>r.tradePnlUsd);
  const totalUsd = pnls.reduce((a,b)=>a+b,0);
  const evUsd = totalUsd/n;
  const avgTpMin = tp.length ? Math.round(tp.reduce((a,r)=>a+r.minutes,0)/tp.length) : 0;
  const avgSlMin = sl.length ? Math.round(sl.reduce((a,r)=>a+r.minutes,0)/sl.length) : 0;
  document.getElementById('sTp').textContent       = (tp.length*100/n).toFixed(1)+'%';
  document.getElementById('sSl').textContent       = (sl.length*100/n).toFixed(1)+'%';
  document.getElementById('sTo').textContent       = (to.length*100/n).toFixed(1)+'%';
  document.getElementById('sTpTime').textContent   = fmtTime(avgTpMin);
  document.getElementById('sSlTime').textContent   = fmtTime(avgSlMin);
  document.getElementById('sEv').textContent       = (evUsd>=0?'+':'')+'$'+evUsd.toFixed(2);
  document.getElementById('sTotalUsd').textContent = (totalUsd>=0?'+':'')+'$'+totalUsd.toFixed(2);
  document.getElementById('sEvUsd').textContent    = (evUsd>=0?'+':'')+'$'+evUsd.toFixed(2);
  document.getElementById('sPeak50').textContent   = '+'+lastPeaks.p50+'%';
  document.getElementById('sPeak75').textContent   = '+'+lastPeaks.p75+'%';
  document.getElementById('sPeak90').textContent   = '+'+lastPeaks.p90+'%';
}

function renderRecommendations(d) {
  document.getElementById('recoCard').style.display='block';
  document.getElementById('recoList').innerHTML = d.top.map((c,i)=>`
    <div class="reco-row">
      <span class="reco-combo">#${i+1} SL ${c.sl}% / TP ${c.tp}%</span>
      <span class="reco-pct">${c.tpPct}% TP · ${c.slPct}% SL</span>
      <span class="reco-ev">EV ${c.evUsd>=0?'+':''}$${c.evUsd.toFixed(2)}/trade</span>
    </div>`).join('');
}

function fmtMc(v) {
  if (!v) return '—';
  if (v>=1e9) return (v/1e9).toFixed(1)+'B';
  if (v>=1e6) return (v/1e6).toFixed(1)+'M';
  return (v/1e3).toFixed(1)+'K';
}
function fmtTs(ts) {
  if (!ts) return '—';
  const d = new Date(ts*1000);
  return d.toLocaleDateString('en-US',{month:'short',day:'numeric'}) + ' ' +
         d.toLocaleTimeString('en-US',{hour:'2-digit',minute:'2-digit',hour12:false});
}
function copyCA(mint, el) {
  navigator.clipboard.writeText(mint);
  const orig = el.textContent;
  el.textContent = 'copied!';
  el.style.color = '#4caf50';
  setTimeout(()=>{ el.textContent = orig; el.style.color = '#5ca8e2'; }, 1200);
}
function fmtTime(m) {
  if (!m) return '—';
  if (m<60) return m+'m';
  const h=Math.floor(m/60), mm=m%60;
  return mm>0?`${h}h ${mm}m`:`${h}h`;
}

loadCached();
</script>
</body>
</html>
""");
        return Content(html.ToString(), "text/html");
    }

    // ── GET /backtest/api/cached ──────────────────────────────────────────

    [HttpGet("api/cached")]
    public async Task<IActionResult> GetCached()
    {
        var caches = (await db.BacktestCache
            .Where(b => b.FetchStatus == "DONE")
            .ToListAsync())
            .OrderByDescending(b => b.AlertTime)
            .ToList();

        var alertIds = caches.Select(c => c.SentAlertId).ToList();
        var alerts   = (await db.SentAlerts
            .Where(a => alertIds.Contains(a.Id))
            .ToListAsync())
            .ToDictionary(a => a.Id);

        var rows = caches.Select(c =>
        {
            alerts.TryGetValue(c.SentAlertId, out var a);
            return new
            {
                id       = c.SentAlertId,
                symbol   = c.TokenSymbol,
                mcap     = a?.MarketCapUsd ?? 0,
                lockPct  = a?.PercentSupply ?? 0,
                alertTime = c.AlertTime.ToUnixTimeSeconds()
            };
        });

        return Ok(rows);
    }

    // ── GET /backtest/api/filter ──────────────────────────────────────────

    [HttpGet("api/filter")]
    public async Task<IActionResult> Filter(
        decimal minMcap = 0, decimal maxMcap = 0,
        decimal minLockPct = 0, int minAge = 0, int maxAge = 9999)
    {
        var all   = await db.SentAlerts.ToListAsync();
        var total = all.Count;

        var matched = all.Where(a =>
            (minMcap    <= 0 || a.MarketCapUsd  >= minMcap)  &&
            (maxMcap    <= 0 || a.MarketCapUsd  <= maxMcap)  &&
            (minLockPct <= 0 || a.PercentSupply >= minLockPct) &&
            MatchesAge(a, minAge, maxAge)
        ).ToList();

        var matchedIds = matched.Select(a => a.Id).ToList();

        var cached = await db.BacktestCache
            .Where(b => matchedIds.Contains(b.SentAlertId) && b.FetchStatus == "DONE")
            .Select(b => b.SentAlertId)
            .ToListAsync();

        var uncachedIds = matchedIds.Except(cached).ToList();

        return Ok(new
        {
            total,
            matched  = matched.Count,
            cached   = cached.Count,
            uncached = uncachedIds.Count,
            ids      = uncachedIds
        });
    }

    // ── POST /backtest/api/run ────────────────────────────────────────────

    [HttpPost("api/run")]
    public IActionResult Run([FromBody] RunRequest req)
    {
        if (HeliusBacktestService.IsRunning)
            return Ok(new { started = false, reason = "already running" });

        helius.StartJob(req.Ids ?? []);
        return Ok(new { started = true });
    }

    // ── GET /backtest/api/status ──────────────────────────────────────────

    [HttpGet("api/status")]
    public IActionResult Status() => Ok(new
    {
        running   = HeliusBacktestService.IsRunning,
        processed = HeliusBacktestService.Processed,
        total     = HeliusBacktestService.TotalToProcess,
        msg       = HeliusBacktestService.CurrentMsg
    });

    // ── POST /backtest/api/analyze ────────────────────────────────────────

    [HttpPost("api/analyze")]
    public async Task<IActionResult> Analyze([FromBody] AnalyzeRequest req)
    {
        var sl = (decimal)req.Sl / 100m;
        var tp = (decimal)req.Tp / 100m;

        var alerts = (await db.SentAlerts.ToListAsync()).ToDictionary(a => a.Id);
        var caches = await db.BacktestCache
            .Where(b => req.Ids == null || req.Ids.Contains(b.SentAlertId))
            .Where(b => b.FetchStatus == "DONE")
            .ToListAsync();

        var rows      = new List<object>();
        int tpHits = 0, slHits = 0, timeouts = 0;
        var tpMins    = new List<int>();
        var slMins    = new List<int>();
        var evPnls    = new List<double>();
        var peakGains = new List<double>();

        foreach (var c in caches)
        {
            var series = JsonSerializer.Deserialize<PricePoint[]>(c.SeriesJson) ?? [];
            if (series.Length == 0) { timeouts++; continue; }

            alerts.TryGetValue(c.SentAlertId, out var alertForImpact);
            var mcap       = alertForImpact?.MarketCapUsd ?? 0;
            var impact     = mcap > 0 ? (decimal)(req.UnitSize * 10) / mcap : 0m;
            var entryPrice = series[0].P * (1 + impact);
            var entryMcap  = mcap + (decimal)(req.UnitSize * 10);
            var slPrice    = entryPrice * (1 - sl);
            var tpPrice    = entryPrice * (1 + tp);
            var outcome    = "TIMEOUT";
            var minutes    = 0;
            var peakGain   = 0m;
            var maxDd      = 0m;
            var exitPct    = 0m;

            foreach (var pt in series)
            {
                var chg = (pt.P - entryPrice) / entryPrice * 100;
                peakGain = Math.Max(peakGain, chg);
                maxDd    = Math.Min(maxDd, chg);

                if (outcome == "TIMEOUT" && pt.P <= slPrice)
                {
                    outcome = "SL";
                    minutes = (int)((pt.T - series[0].T) / 60);
                    exitPct = chg;
                    break;
                }
                if (outcome == "TIMEOUT" && pt.P >= tpPrice)
                {
                    outcome = "TP";
                    minutes = (int)((pt.T - series[0].T) / 60);
                    exitPct = chg; // actual tick price — can be above tp if gapped
                    break;
                }
            }

            var exitMcap = entryMcap * (1 + exitPct / 100);
            peakGains.Add((double)peakGain);

            // TP: capped at tp% (limit order); SL: actual exit (gapped through)
            var tradePnlPct = outcome == "TP" ? (double)(tp * 100) : (double)exitPct;
            var tradePnlUsd = req.UnitSize * tradePnlPct / 100.0;
            if (outcome == "TP")        { tpHits++; tpMins.Add(minutes); evPnls.Add(tradePnlUsd); }
            else if (outcome == "SL")   { slHits++; slMins.Add(minutes); evPnls.Add(tradePnlUsd); }
            else timeouts++;

            rows.Add(new
            {
                alertId      = c.SentAlertId,
                symbol       = c.TokenSymbol,
                mint         = c.TokenMint,
                alertTime    = alertForImpact?.SentAt.ToUnixTimeSeconds() ?? 0,
                notifMcap    = (double)mcap,
                entryMcap    = (double)entryMcap,
                exitMcap     = outcome == "TIMEOUT" ? 0 : (double)exitMcap,
                lockPct      = alertForImpact?.PercentSupply ?? 0,
                outcome,
                minutes,
                exitPct      = (double)exitPct,
                tradePnlUsd,
                peakGain     = (double)peakGain,
                maxDd        = (double)maxDd
            });
        }

        peakGains.Sort();
        var n        = caches.Count;
        var evUsd    = evPnls.Count > 0 ? evPnls.Average() : 0;
        var totalUsd = evPnls.Sum();
        var p50      = peakGains.Count > 0 ? peakGains[(int)(peakGains.Count * 0.50)] : 0;
        var p75      = peakGains.Count > 0 ? peakGains[(int)(peakGains.Count * 0.75)] : 0;
        var p90      = peakGains.Count > 0 ? peakGains[(int)(peakGains.Count * 0.90)] : 0;

        return Ok(new
        {
            rows,
            summary = new
            {
                tpPct    = n > 0 ? Math.Round(tpHits  * 100.0 / n, 1) : 0,
                slPct    = n > 0 ? Math.Round(slHits  * 100.0 / n, 1) : 0,
                toPct    = n > 0 ? Math.Round(timeouts * 100.0 / n, 1) : 0,
                avgTpMin = tpMins.Count > 0 ? (int)tpMins.Average() : 0,
                avgSlMin = slMins.Count > 0 ? (int)slMins.Average() : 0,
                evUsd    = Math.Round(evUsd, 2),
                totalUsd = Math.Round(totalUsd, 2),
                peakP50  = Math.Round(p50, 0),
                peakP75  = Math.Round(p75, 0),
                peakP90  = Math.Round(p90, 0)
            }
        });
    }

    // ── POST /backtest/api/recommend ──────────────────────────────────────

    [HttpPost("api/recommend")]
    public async Task<IActionResult> Recommend([FromBody] RecommendRequest req)
    {
        var caches = await db.BacktestCache
            .Where(b => req.Ids == null || req.Ids.Contains(b.SentAlertId))
            .Where(b => b.FetchStatus == "DONE")
            .ToListAsync();

        var alertIds  = caches.Select(c => c.SentAlertId).ToList();
        var alertMap  = (await db.SentAlerts.Where(a => alertIds.Contains(a.Id)).ToListAsync())
                            .ToDictionary(a => a.Id);

        var seriesWithMcap = caches
            .Select(c => {
                alertMap.TryGetValue(c.SentAlertId, out var a);
                var pts    = JsonSerializer.Deserialize<PricePoint[]>(c.SeriesJson) ?? [];
                var mcap   = a?.MarketCapUsd ?? 0;
                var impact = mcap > 0 ? (decimal)(req.UnitSize * 10) / mcap : 0m;
                return (pts, impact);
            })
            .Where(x => x.pts.Length > 0)
            .ToList();

        var results = new List<object>();

        foreach (var slPct in new[] { 10, 15, 20, 25, 30, 40, 50 })
        foreach (var tpPct in new[] { 50, 75, 100, 150, 200, 300, 500 })
        {
            var sl = slPct / 100m;
            var tp = tpPct / 100m;
            var pnls = new List<double>();

            foreach (var (s, impact) in seriesWithMcap)
            {
                var entry   = s[0].P * (1 + impact);
                var slPrice = entry * (1 - sl);
                var tpPrice = entry * (1 + tp);
                var hit     = false;

                foreach (var pt in s)
                {
                    if (pt.P <= slPrice)
                    {
                        var actualPct = (pt.P - entry) / entry * 100;
                        pnls.Add(req.UnitSize * (double)actualPct / 100.0);
                        hit = true; break;
                    }
                    if (pt.P >= tpPrice)
                    {
                        pnls.Add(req.UnitSize * (double)tp);
                        hit = true; break;
                    }
                }
                // timeouts: no pnl added
            }

            var n     = seriesWithMcap.Count;
            var evUsd = pnls.Count > 0 ? pnls.Average() : 0;
            var tpHit = pnls.Count(p => p > 0);
            var slHit = pnls.Count(p => p < 0);

            results.Add(new
            {
                sl    = slPct,
                tp    = tpPct,
                tpPct = Math.Round(tpHit * 100.0 / n, 1),
                slPct = Math.Round(slHit * 100.0 / n, 1),
                evUsd = Math.Round(evUsd, 2)
            });
        }

        var top = results
            .OrderByDescending(r => ((dynamic)r).evUsd)
            .Take(5)
            .ToList();

        return Ok(new { top });
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static bool MatchesAge(SentAlert a, int minDays, int maxDays)
    {
        if (minDays <= 0 && maxDays >= 9999) return true;
        if (a.PairCreatedAt is null) return true; // unknown age → include
        var ageDays = (a.SentAt - a.PairCreatedAt.Value).TotalDays;
        return ageDays >= minDays && ageDays <= maxDays;
    }
}

public class RunRequest       { public List<int>? Ids { get; set; } }
public class AnalyzeRequest   { public double Sl { get; set; } public double Tp { get; set; } public double UnitSize { get; set; } = 40; public List<int>? Ids { get; set; } }
public class RecommendRequest { public double UnitSize { get; set; } = 40; public List<int>? Ids { get; set; } }
