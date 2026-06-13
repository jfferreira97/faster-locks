using System.Text;
using System.Text.Json;
using FasterAlerts.Data;
using FasterAlerts.Models;
using FasterAlerts.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FasterAlerts.Controllers;

[ApiController]
[Route("copytrade")]
public class TradingDashboardController(
    AutoTradeService autoTrade,
    PumpFunMonitorService monitor,
    TradingEventLog eventLog,
    DexScreenerService dexscreener,
    AppDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions CamelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [HttpGet]
    public ContentResult Dashboard() => Content(BuildHtml(), "text/html", Encoding.UTF8);

    [HttpGet("api/state")]
    public async Task<IActionResult> State()
    {
        var settings = await autoTrade.GetSettingsAsync();
        var active   = monitor.GetActiveSnapshots();
        var closed   = await db.Trades.Where(t => t.Status == "Closed" || t.Status == "SellFailed")
            .OrderByDescending(t => t.Id).Take(100).ToListAsync();
        var closedIds = closed.Select(t => t.Id).ToList();
        var tpOrders = closedIds.Count > 0
            ? await db.TpOrders.Where(o => closedIds.Contains(o.TradeId)).ToListAsync()
            : new List<TpOrder>();
        var logs     = eventLog.GetRecent();
        return Ok(new { settings, active, closed, logs, tpOrders });
    }

    [HttpPost("api/settings")]
    public async Task<IActionResult> SaveSettings([FromBody] TradingSettings settings)
    {
        await autoTrade.SaveSettingsAsync(settings);
        monitor.UpdateTpLevels(settings.TakeProfitLevels ?? "");
        return Ok();
    }

    [HttpPost("api/enabled")]
    public async Task<IActionResult> SetEnabled([FromBody] EnabledDto dto)
    {
        var settings = await autoTrade.GetSettingsAsync();
        settings.Enabled = dto.Enabled;
        await autoTrade.SaveSettingsAsync(settings);
        return Ok(new { enabled = settings.Enabled });
    }

    [HttpPost("api/close/{mint}")]
    public IActionResult ClosePosition(string mint)
    {
        monitor.StopMonitor(mint);
        return Ok();
    }

    [HttpPost("api/partial/{mint}")]
    public async Task<IActionResult> PartialClose(string mint, [FromBody] PartialCloseDto dto)
    {
        await monitor.ManualPartialSellAsync(mint, dto.Percent);
        return Ok();
    }

    [HttpDelete("api/trades/clear-all")]
    public async Task<IActionResult> ClearAllTrades()
    {
        monitor.CancelAllMonitors();
        autoTrade.ClearAllMints();
        await db.TpOrders.ExecuteDeleteAsync();
        await db.Trades.ExecuteDeleteAsync();
        return Ok(new { cleared = true });
    }

    [HttpPost("api/trade/manual")]
    public async Task<IActionResult> ManualTrade([FromBody] ManualTradeDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Mint)) return BadRequest("mint required");
        var alert = new StreamAlert { TokenMint = dto.Mint.Trim(), Timestamp = DateTimeOffset.UtcNow };
        await dexscreener.EnrichAsync(alert);
        var (ok, msg) = await autoTrade.ManualBuyAsync(alert);
        return Ok(new { ok, msg });
    }

    public record EnabledDto(bool Enabled);
    public record ManualTradeDto(string Mint);
    public record PartialCloseDto(int Percent);

    private static string BuildHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>FASTER — CopyTrade</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;background:#0e1621;color:#e8e9ea;min-height:100vh;padding:20px 16px}
.page{max-width:1200px;margin:0 auto}
.top{display:flex;align-items:center;justify-content:space-between;margin-bottom:18px}
h1{font-size:15px;font-weight:600}
.master-wrap{display:flex;align-items:center;gap:14px;background:#17212b;border-radius:12px;padding:14px 20px;margin-bottom:18px;cursor:pointer;user-select:none;transition:.2s;border:2px solid #1e2d3d}
.master-wrap.on{border-color:#2f9e44;background:#0f1f12}
.master-wrap.off{border-color:#6b2121;background:#1a0f0f}
.master-wrap:hover{filter:brightness(1.12)}
.master-label{flex:1}
.master-label .ml{font-size:18px;font-weight:700;letter-spacing:.02em}
.master-label .ms{font-size:11px;color:#6c7883;margin-top:2px}
.big-knob{width:64px;height:34px;background:#2b3a4a;border-radius:17px;position:relative;transition:.25s;flex-shrink:0}
.big-knob.on{background:#2f9e44}
.big-knob::after{content:"";position:absolute;top:4px;left:4px;width:26px;height:26px;background:#fff;border-radius:50%;transition:.25s;box-shadow:0 1px 4px #0006}
.big-knob.on::after{left:34px}
.stats{display:flex;gap:12px;margin-bottom:18px;flex-wrap:wrap}
.stat{background:#17212b;border-radius:8px;padding:12px 16px;flex:1;min-width:100px}
.stat-val{font-size:20px;font-weight:700;margin-bottom:2px}
.stat-lbl{font-size:10px;color:#6c7883;text-transform:uppercase;letter-spacing:.05em}
.pos{color:#6bcb6b}.neg{color:#e05c5c}.neu{color:#6c7883}
.tabs{display:flex;gap:2px;margin-bottom:0;border-bottom:1px solid #1e2d3d}
.tab{padding:8px 18px;font-size:12px;cursor:pointer;color:#6c7883;border-bottom:2px solid transparent;margin-bottom:-1px;transition:.15s;user-select:none}
.tab.active{color:#5ca8e2;border-bottom-color:#5ca8e2}
.panel{background:#17212b;border-radius:0 8px 8px 8px;padding:16px;display:none}
.panel.active{display:block}
table{width:100%;border-collapse:collapse;font-size:12px}
th{text-align:left;color:#6c7883;font-weight:500;padding:7px 10px;border-bottom:1px solid #1e2d3d;font-size:11px;text-transform:uppercase;letter-spacing:.04em}
td{padding:9px 10px;border-bottom:1px solid #0e162166;vertical-align:middle}
tr:last-child td{border-bottom:none}
.sym{font-weight:600;font-size:13px}
.sub{font-size:10px;color:#6c7883;margin-top:2px}
.pbar{height:3px;border-radius:2px;margin-top:4px;background:#1e2d3d}
.pfill{height:3px;border-radius:2px}
.btn{background:#2b5278;border:none;border-radius:5px;color:#e8e9ea;font-size:11px;padding:4px 10px;cursor:pointer;transition:.15s}
.btn:hover{background:#5ca8e2}
.btn-red{background:#6b2121}.btn-red:hover{background:#c03333}
.btn-save{background:#2f5e2f;padding:7px 18px;font-size:13px}.btn-save:hover{background:#2f9e44}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;margin-bottom:12px}
label{font-size:11px;color:#6c7883;display:block;margin-bottom:4px}
input[type=number]{background:#0e1621;border:1px solid #2b5278;border-radius:6px;color:#e8e9ea;padding:6px 10px;font-size:13px;width:110px;outline:none}
input[type=text],input[type=password]{background:#0e1621;border:1px solid #2b5278;border-radius:6px;color:#e8e9ea;padding:6px 10px;font-size:12px;font-family:monospace;outline:none;width:100%}
input:focus{border-color:#5ca8e2}
.field-wide{flex:1;min-width:280px}
.toggle{display:flex;align-items:center;gap:8px;cursor:pointer;user-select:none}
.toggle input{display:none}
.knob{width:38px;height:21px;background:#2b5278;border-radius:11px;position:relative;transition:.2s}
.toggle input:checked+.knob{background:#2f9e44}
.knob::after{content:"";position:absolute;top:3px;left:3px;width:15px;height:15px;background:#fff;border-radius:50%;transition:.2s}
.toggle input:checked+.knob::after{left:20px}
.empty{color:#6c7883;font-size:12px;padding:20px 0;text-align:center}
.log-entry{font-size:11px;padding:4px 0;border-bottom:1px solid #0e162144;display:flex;gap:10px}
.log-t{color:#6c7883;flex-shrink:0;font-variant-numeric:tabular-nums}
.log-INFO{color:#5ca8e2}.log-WARN{color:#f0a33c}.log-ERROR{color:#e05c5c}
.refresh-ts{font-size:10px;color:#6c7883;text-align:right;margin-bottom:6px}
.mc-chip{font-size:10px;background:#1e2d3d;color:#5ca8e2;padding:1px 6px;border-radius:3px;margin-left:4px}
.status-ok{font-size:10px;color:#6bcb6b}.status-fail{font-size:10px;color:#e05c5c}
details>summary{list-style:none;cursor:pointer}.notes-box{font-size:10px;font-family:monospace;color:#6c7883;white-space:pre-wrap;padding:6px 0 2px;line-height:1.6}
.err-badge{background:#6b2121;color:#e05c5c;font-size:9px;padding:1px 5px;border-radius:3px;margin-left:4px;cursor:pointer}
.src-manual{font-size:9px;background:#3d2060;color:#b89fdd;padding:1px 5px;border-radius:3px;margin-left:4px;vertical-align:middle}
.src-auto{font-size:9px;background:#1e2d3d;color:#6c7883;padding:1px 5px;border-radius:3px;margin-left:4px;vertical-align:middle}
.manual-buy{display:flex;gap:8px;align-items:center;margin-bottom:12px;padding:10px 12px;background:#0e1621;border-radius:8px;border:1px solid #1e2d3d}
.mon-ws{font-size:9px;background:#0f3320;color:#6bcb6b;padding:1px 5px;border-radius:3px;margin-left:4px;vertical-align:middle}
.mon-poll{font-size:9px;background:#3b2a00;color:#f0a33c;padding:1px 5px;border-radius:3px;margin-left:4px;vertical-align:middle}
.mon-none{font-size:9px;background:#1e2d3d;color:#6c7883;padding:1px 5px;border-radius:3px;margin-left:4px;vertical-align:middle}
.mon-age{font-size:9px;color:#6c7883;margin-left:3px}
</style>
</head>
<body>
<div class="page">
  <div class="top" style="margin-bottom:14px">
    <h1>⚡ CopyTrade</h1>
    <span id="refresh-ts" class="refresh-ts" style="margin-bottom:0"></span>
  </div>

  <div id="master-toggle" class="master-wrap off" onclick="toggleEnabled()">
    <div class="master-label">
      <div class="ml" id="master-label">AUTO-TRADE OFF</div>
      <div class="ms">Click to enable — will fire on next qualifying stream lock</div>
    </div>
    <div id="big-knob" class="big-knob"></div>
  </div>

  <div class="stats" id="stats"></div>

  <div class="tabs">
    <div class="tab active" onclick="switchTab('active')">Active Positions</div>
    <div class="tab" onclick="switchTab('closed')">Closed Trades</div>
    <div class="tab" onclick="switchTab('settings')">Settings</div>
    <div class="tab" onclick="switchTab('log')">Log</div>
  </div>

  <div class="panel active" id="panel-active">
    <div class="manual-buy">
      <input type="text" id="manualMint" placeholder="Paste contract address to buy manually…" style="flex:1;font-family:monospace">
      <button class="btn" id="manualBuyBtn" onclick="manualBuy()">Buy Manually</button>
      <span id="manualBuyStatus" style="font-size:11px;font-family:monospace"></span>
    </div>
    <div id="active-body"></div>
  </div>

  <div class="panel" id="panel-closed">
    <div style="display:flex;justify-content:flex-end;margin-bottom:10px">
      <button class="btn btn-red" onclick="clearAllTrades()">Clear All Trade Data</button>
    </div>
    <div id="closed-body"></div>
  </div>

  <div class="panel" id="panel-settings">
    <div style="color:#6c7883;font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin-bottom:10px">Trade Parameters</div>
    <div class="row">
      <div><label>Unit Size (SOL)</label><input type="number" id="solAmount" step="0.01" min="0.01"></div>
      <div><label>Trailing Stop %</label><input type="number" id="trailingStop" step="1" min="1" max="99"></div>
    </div>
    <div style="color:#6c7883;font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin:12px 0 10px">Entry Filters</div>
    <div class="row">
      <div><label>Min % Locked</label><input type="number" id="minPctLocked" step="0.1" min="0"></div>
      <div><label>Min MC ($)</label><input type="number" id="minMc" step="1" min="0"></div>
      <div><label>Max MC ($)</label><input type="number" id="maxMc" step="100" min="0"></div>
      <div><label>Max Token Age (hrs)</label><input type="number" id="maxAge" step="1" min="1"></div>
      <div><label>Min Vesting (days)</label><input type="number" id="minVesting" step="1" min="0"></div>
    </div>
    <div style="color:#6c7883;font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin:12px 0 10px">Take Profit Levels</div>
    <div class="row">
      <div class="field-wide">
        <label>Levels — format: <code style="color:#5ca8e2">gainPct:sellPct,gainPct:sellPct</code> &nbsp; e.g. <code style="color:#5ca8e2">60:50,120:50</code> = sell 50% at +60%, sell 50% of remaining at +120%</label>
        <input type="text" id="tpLevels" placeholder="Leave blank to disable — e.g. 60:50,120:50">
      </div>
    </div>
    <div style="color:#6c7883;font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin:12px 0 10px">Wallet</div>
    <div class="row">
      <div class="field-wide"><label>Wallet Address (public)</label><input type="text" id="walletAddress" placeholder="Solana public key…"></div>
    </div>
    <div class="row">
      <div class="field-wide"><label>Private Key (Base58)</label>
        <div style="display:flex;gap:6px">
          <input type="password" id="walletKey" placeholder="Leave blank to keep existing…">
          <button class="btn" style="flex-shrink:0;align-self:center" onclick="toggleKey(this)">Show</button>
        </div>
      </div>
    </div>
    <button class="btn btn-save" onclick="saveSettings()">Save Settings</button>
    <span id="save-msg" style="font-size:12px;color:#6bcb6b;margin-left:10px;display:none">Saved ✓</span>
  </div>

  <div class="panel" id="panel-log">
    <div id="log-body"></div>
  </div>
</div>

<script>
let _state = null;
let _tab = 'active';

function switchTab(name) {
  _tab = name;
  document.querySelectorAll('.tab').forEach((t,i) => t.classList.toggle('active', ['active','closed','settings','log'][i]===name));
  document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
  document.getElementById('panel-'+name).classList.add('active');
}

async function load() {
  try {
    const r = await fetch('/copytrade/api/state');
    _state = await r.json();
    render();
    document.getElementById('refresh-ts').textContent = 'Last updated: ' + new Date().toLocaleTimeString();
  } catch(e) { console.error(e); }
}

function esc(s) { return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function fmt(n, dec=4) { return n === null || n === undefined ? '—' : n.toFixed(dec); }
function fmtE(n) { return n > 0 ? n.toExponential(3) : '—'; }
function fmtMc(n) {
  if (!n || n <= 0) return '—';
  if (n >= 1e6) return '$' + (n/1e6).toFixed(2) + 'M';
  if (n >= 1e3) return '$' + (n/1e3).toFixed(1) + 'K';
  return '$' + n.toFixed(0);
}
function elapsed(from, to) {
  const ms = (to ? new Date(to) : new Date()) - new Date(from);
  const s=Math.floor(ms/1000), m=Math.floor(s/60), h=Math.floor(m/60), d=Math.floor(h/24);
  if (d>0) return d+'d '+h%24+'h';
  if (h>0) return h+'h '+m%60+'m';
  if (m>0) return m+'m '+s%60+'s';
  return s+'s';
}
function pnlClass(v) { return v > 0 ? 'pos' : v < 0 ? 'neg' : 'neu'; }

function render() {
  const { settings, active, closed, logs } = _state;

  // Master toggle
  const on = settings.enabled;
  document.getElementById('master-toggle').className = 'master-wrap ' + (on ? 'on' : 'off');
  document.getElementById('big-knob').className      = 'big-knob '   + (on ? 'on' : '');
  document.getElementById('master-label').textContent = on ? 'AUTO-TRADE ON' : 'AUTO-TRADE OFF';

  // Stats
  const totalPnl = closed.reduce((s,t) => s + (t.pnlSol||0), 0);
  const wins     = closed.filter(t => (t.pnlSol||0) > 0).length;
  const wr       = closed.length ? Math.round(wins/closed.length*100) : 0;
  const deployed = active.reduce((s,a) => s + a.solSpent, 0);
  document.getElementById('stats').innerHTML = [
    ['stat-val ' + pnlClass(totalPnl), (totalPnl>=0?'+':'')+fmt(totalPnl,4)+' SOL', 'Total PnL'],
    ['stat-val', closed.length, 'Trades'],
    ['stat-val', wr+'%', 'Win Rate'],
    ['stat-val', active.length, 'Active'],
    ['stat-val', fmt(deployed,3)+' SOL', 'Deployed'],
  ].map(([cls,val,lbl]) => `<div class="stat"><div class="${cls}">${val}</div><div class="stat-lbl">${lbl}</div></div>`).join('');

  // Settings inputs (only update if tab not focused to avoid clobbering user edits)
  if (_tab !== 'settings' || !document.activeElement.matches('input')) {
    document.getElementById('solAmount').value    = (settings.buySolLamports/1e9).toFixed(3);
    document.getElementById('trailingStop').value = settings.trailingStopPercent;
    document.getElementById('minPctLocked').value = settings.minPercentLocked;
    document.getElementById('minMc').value        = settings.minMarketCapUsd;
    document.getElementById('maxMc').value        = settings.maxMarketCapUsd;
    document.getElementById('maxAge').value       = settings.maxTokenAgeHours;
    document.getElementById('minVesting').value   = settings.minVestingDays;
    document.getElementById('tpLevels').value     = settings.takeProfitLevels || '';
    document.getElementById('walletAddress').value = settings.walletAddress || '';
    // Never overwrite the key field once the user has touched it; only populate on first load
    const keyField = document.getElementById('walletKey');
    if (!keyField.dataset.touched) keyField.placeholder = settings.walletPrivateKeyBase58 ? 'Key set — paste new to replace' : 'Paste Base58 private key…';
  }

  // Active
  const activeDiv = document.getElementById('active-body');
  if (!active.length) {
    activeDiv.innerHTML = '<div class="empty">No active positions</div>';
  } else {
    const rows = active.map(p => {
      const upnl    = (p.currentPriceSol - p.entryPriceSol) * (p.tokenAmount / 1e6);
      const upnlPct = p.entryPriceSol > 0 ? (p.currentPriceSol/p.entryPriceSol - 1)*100 : 0;
      const athMult = p.entryPriceSol > 0 ? p.highestPriceSol/p.entryPriceSol : 1;
      const curMult = p.entryPriceSol > 0 ? p.currentPriceSol/p.entryPriceSol : 1;
      const distPct  = p.highestPriceSol > 0 ? (p.currentPriceSol-p.stopPriceSol)/p.currentPriceSol*100 : 0;
      const bagPct   = p.tokenAmount > 0 ? Math.round(p.remainingTokenAmount / p.tokenAmount * 100) : 100;
      const curMc   = p.entryMarketCapUsd * curMult;
      const range   = p.highestPriceSol - p.stopPriceSol;
      const barPct  = range > 0 ? Math.max(0, Math.min(100, (p.currentPriceSol-p.stopPriceSol)/range*100)) : 100;
      const notes   = p.notes || [];
      const errCount= notes.filter(n => n.includes('error') || n.includes('Error') || n.includes('disconnect')).length;
      const notesHtml = notes.length
        ? `<details style="margin-top:4px"><summary style="font-size:10px;color:${errCount?'#e05c5c':'#6c7883'}">${errCount?'⚠ ':''}${notes.length} event${notes.length>1?'s':''}</summary><div class="notes-box">${notes.map(n=>esc(n)).join('\n')}</div></details>` : '';
      const athMc  = p.entryMarketCapUsd * athMult;
      const stopMc = p.entryMarketCapUsd * (p.entryPriceSol > 0 ? p.stopPriceSol/p.entryPriceSol : 1);
      const srcBadge = p.source === 'Manual' ? `<span class="src-manual">MANUAL</span>` : `<span class="src-auto">AUTO</span>`;
      const ageSec  = p.lastPriceUpdate && p.lastPriceUpdate !== '0001-01-01T00:00:00+00:00'
        ? Math.floor((Date.now() - new Date(p.lastPriceUpdate)) / 1000) : null;
      const ageStr  = ageSec !== null ? (ageSec < 60 ? ageSec+'s ago' : Math.floor(ageSec/60)+'m ago') : '';
      const monBadge = p.priceSource === 'WS'
        ? `<span class="mon-ws">⚡ WS</span><span class="mon-age">${ageStr}</span>`
        : p.priceSource === 'Poll'
        ? `<span class="mon-poll">⟳ Poll</span><span class="mon-age">${ageStr}</span>`
        : `<span class="mon-none">○ waiting</span>`;
      const hist = (p.priceHistory || []).slice().reverse();
      const histHtml = hist.length ? `<details style="margin-top:5px">
        <summary style="font-size:10px;color:#6c7883;cursor:pointer">${hist.length} price ticks</summary>
        <div style="max-height:180px;overflow-y:auto;margin-top:4px">
          ${hist.map(h => {
            const pct = (h.multFromEntry - 1)*100;
            const col = pct >= 0 ? '#6bcb6b' : '#e05c5c';
            const t = new Date(h.time).toLocaleTimeString();
            return `<div style="display:flex;gap:10px;padding:2px 0;border-bottom:1px solid #0e162133;font-size:10px;font-family:monospace">
              <span style="color:#6c7883;flex-shrink:0">${t}</span>
              <span>${fmtMc(h.marketCapUsd)}</span>
              <span style="color:${col}">${pct>=0?'+':''}${pct.toFixed(1)}%</span>
            </div>`;
          }).join('')}
        </div>
      </details>` : '';
      return `<tr>
        <td><span class="sym">$${p.symbol}</span>${srcBadge}${monBadge}<div class="sub">${p.mint.slice(0,8)}…</div>${notesHtml}${histHtml}</td>
        <td>${fmtMc(p.entryMarketCapUsd)}<div class="sub">entry</div></td>
        <td>${fmtMc(curMc)}<div class="sub">${curMult.toFixed(2)}x</div></td>
        <td>${fmtMc(athMc)}<div class="sub">ATH ${athMult.toFixed(2)}x</div></td>
        <td>${fmtMc(stopMc)}<div class="pbar"><div class="pfill" style="width:${barPct}%;background:${barPct<30?'#e05c5c':barPct<60?'#f0a33c':'#5ca8e2'}"></div></div><div class="sub">${distPct.toFixed(1)}% away</div></td>
        <td><div class="pbar"><div class="pfill" style="width:${bagPct}%;background:${bagPct>66?'#5ca8e2':bagPct>33?'#f0a33c':'#e05c5c'}"></div></div><div class="sub">${bagPct}% bag</div></td>
        <td class="${pnlClass(upnl)}">${upnl>=0?'+':''}${fmt(upnl,4)} SOL<div class="sub">${upnlPct>=0?'+':''}${upnlPct.toFixed(1)}%</div></td>
        <td style="color:#6c7883">${elapsed(p.entryTime)}</td>
        <td>
          <div style="display:flex;flex-direction:column;gap:4px;align-items:flex-start">
            <button class="btn btn-red" onclick="closePos('${p.mint}')">Close All</button>
            <div style="display:flex;gap:4px;align-items:center">
              <input type="number" id="pct-${p.mint}" value="50" min="1" max="99" style="width:52px;padding:3px 6px;font-size:11px">
              <button class="btn" style="font-size:11px;padding:3px 8px" onclick="partialSell('${p.mint}')">Sell %</button>
            </div>
          </div>
        </td>
      </tr>`;
    }).join('');
    activeDiv.innerHTML = `<table>
      <thead><tr><th>Token</th><th>Entry MC</th><th>Current MC</th><th>ATH MC</th><th>Stop MC</th><th>Bag</th><th>uPnL</th><th>Age</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table>`;
  }

  // Closed
  const closedDiv = document.getElementById('closed-body');
  if (!closed.length) {
    closedDiv.innerHTML = '<div class="empty">No closed trades yet</div>';
  } else {
    const tpMap = {};
    (_state.tpOrders || []).forEach(o => { (tpMap[o.tradeId] = tpMap[o.tradeId] || []).push(o); });
    const rows = closed.map(t => {
      const pnl      = t.pnlSol || 0;
      const mult     = t.entryPriceSol > 0 && t.exitPriceSol ? t.exitPriceSol/t.entryPriceSol : null;
      const dur      = t.closeTime ? elapsed(t.entryTime, t.closeTime) : '—';
      const sellSig  = t.sellSignature;
      const buySig   = t.buySignature;
      const failed   = t.status === 'SellFailed';
      const statusEl = failed
        ? `<span class="status-fail">⚠ sell failed</span>`
        : (sellSig ? `<span class="status-ok">✓ sold</span>` : '');
      const noteLines = (t.notes || '').split('\n').filter(Boolean);
      const errLines  = noteLines.filter(n => n.toLowerCase().includes('fail') || n.toLowerCase().includes('error') || n.toLowerCase().includes('disconnect'));
      const notesHtml = noteLines.length
        ? `<details><summary style="font-size:10px;color:${errLines.length?'#e05c5c':'#6c7883'};margin-top:4px">${errLines.length?'⚠ ':''} ${noteLines.length} event${noteLines.length>1?'s':''}</summary><div class="notes-box">${noteLines.map(n=>esc(n)).join('\n')}</div></details>`
        : '';
      const srcBadge = t.source === 'Manual' ? `<span class="src-manual">MANUAL</span>` : `<span class="src-auto">AUTO</span>`;
      const tps = tpMap[t.id] || [];
      const tpSol = tps.reduce((s, o) => s + (o.solReceived || 0), 0);
      const tpLinks = tps.map(o => {
        const label = o.threshold > 0 ? `TP+${o.threshold}%` : 'Manual';
        const solPart = o.solReceived > 0 ? ` (${o.solReceived.toFixed(3)}◎)` : '';
        return o.signature
          ? `<a href="https://solscan.io/tx/${o.signature}" target="_blank" class="btn" style="margin-left:4px;background:#1e3d1e">${label}${solPart}</a>`
          : `<span style="font-size:10px;color:#e05c5c;margin-left:4px">${label} failed</span>`;
      }).join('');
      const tpNote   = tpSol > 0 ? `<div class="sub" style="color:#6bcb6b">+${tpSol.toFixed(3)}◎ from TPs</div>` : '';
      const athNote  = t.athMarketCapUsd  > 0 ? `<div class="sub">ATH ${fmtMc(t.athMarketCapUsd)}</div>`  : '';
      const exitMcNote = t.exitMarketCapUsd > 0 ? `<div class="sub">${fmtMc(t.exitMarketCapUsd)}</div>` : '';
      const alertMeta = [
        t.vestingDays  > 0 ? `${t.vestingDays}d vest`      : '',
        t.percentSupply > 0 ? `${t.percentSupply.toFixed(1)}% locked` : '',
        t.lockedUsd    > 0 ? `~${fmtMc(t.lockedUsd)} locked` : '',
      ].filter(Boolean).join(' · ');
      return `<tr>
        <td><span class="sym">$${t.tokenSymbol}</span>${srcBadge}<span class="mc-chip">${fmtMc(t.entryMarketCapUsd)}</span><div class="sub">${t.tokenMint.slice(0,8)}…</div>${alertMeta ? `<div class="sub" style="color:#5ca8e2">${alertMeta}</div>` : ''}</td>
        <td>${fmtE(t.entryPriceSol)}</td>
        <td>${fmtE(t.exitPriceSol)}${exitMcNote}</td>
        <td class="${pnlClass(pnl)}">${pnl>=0?'+':''}${fmt(pnl,4)} SOL${tpNote}${athNote}<div class="sub">${mult ? (mult>=1?'+':'')+(((mult-1)*100).toFixed(1))+'%' : '—'}</div></td>
        <td style="color:#6c7883">${dur}</td>
        <td style="color:#6c7883">${new Date(t.entryTime).toLocaleString()}</td>
        <td>
          ${statusEl}
          ${buySig  ? `<a href="https://solscan.io/tx/${buySig}"  target="_blank" class="btn" style="margin-left:4px">buy</a>`  : ''}
          ${tpLinks}
          ${sellSig ? `<a href="https://solscan.io/tx/${sellSig}" target="_blank" class="btn" style="margin-left:4px">sell</a>` : ''}
          ${notesHtml}
        </td>
      </tr>`;
    }).join('');
    closedDiv.innerHTML = `<table>
      <thead><tr><th>Token</th><th>Entry</th><th>Exit</th><th>PnL</th><th>Duration</th><th>Opened</th><th>Txns</th></tr></thead>
      <tbody>${rows}</tbody></table>`;
  }

  // Log
  const logDiv = document.getElementById('log-body');
  if (!logs.length) {
    logDiv.innerHTML = '<div class="empty">No log entries yet</div>';
  } else {
    logDiv.innerHTML = logs.map(l => {
      const d = new Date(l.time);
      const t = d.toLocaleDateString([], {month:'2-digit',day:'2-digit'}) + ' ' + d.toLocaleTimeString();
      return `<div class="log-entry"><span class="log-t">${t}</span><span class="log-${l.level}">[${l.level}]</span><span>${esc(l.message)}</span></div>`;
    }).join('');
  }
}

async function saveSettings() {
  const payload = {
    id: 1,
    enabled:             _state?.settings?.enabled ?? false,
    buySolLamports:      Math.round(parseFloat(document.getElementById('solAmount').value)*1e9),
    trailingStopPercent: parseInt(document.getElementById('trailingStop').value),
    minPercentLocked:    parseFloat(document.getElementById('minPctLocked').value),
    minMarketCapUsd:     parseFloat(document.getElementById('minMc').value),
    maxMarketCapUsd:     parseFloat(document.getElementById('maxMc').value),
    maxTokenAgeHours:    parseInt(document.getElementById('maxAge').value),
    minVestingDays:      parseInt(document.getElementById('minVesting').value) || 0,
    takeProfitLevels:    document.getElementById('tpLevels').value.trim(),
    walletAddress:       document.getElementById('walletAddress').value.trim(),
    walletPrivateKeyBase58: document.getElementById('walletKey').value.trim(),
  };
  await fetch('/copytrade/api/settings', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
  const msg = document.getElementById('save-msg');
  msg.style.display = 'inline';
  setTimeout(() => msg.style.display='none', 2000);
  await load();
}

let _toggling = false;
async function toggleEnabled() {
  if (_toggling) return;
  _toggling = true;
  const next = !(_state?.settings?.enabled ?? false);
  // Optimistic UI
  document.getElementById('master-toggle').className = 'master-wrap ' + (next ? 'on' : 'off');
  document.getElementById('big-knob').className      = 'big-knob '   + (next ? 'on' : '');
  document.getElementById('master-label').textContent = next ? 'AUTO-TRADE ON' : 'AUTO-TRADE OFF';
  try {
    await fetch('/copytrade/api/enabled', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:next})});
    await load();
  } catch(e) { console.error(e); }
  _toggling = false;
}

function toggleKey(btn) {
  const inp = document.getElementById('walletKey');
  inp.dataset.touched = '1';
  if (inp.type === 'password') { inp.type = 'text'; btn.textContent = 'Hide'; }
  else { inp.type = 'password'; btn.textContent = 'Show'; }
}

async function closePos(mint) {
  if (!confirm('Close entire position and sell all tokens?')) return;
  await fetch('/copytrade/api/close/'+mint, {method:'POST'});
  await load();
}

async function partialSell(mint) {
  const pct = parseInt(document.getElementById('pct-'+mint)?.value) || 50;
  if (!confirm('Sell ' + pct + '% of remaining bag for ' + mint.slice(0,8) + '…?')) return;
  await fetch('/copytrade/api/partial/'+mint, {
    method: 'POST',
    headers: {'Content-Type':'application/json'},
    body: JSON.stringify({percent: pct})
  });
  await load();
}

async function clearAllTrades() {
  if (!confirm('Delete ALL trade records and TP orders? This cannot be undone.\n\nActive monitors will be cancelled (no sells fired).')) return;
  await fetch('/copytrade/api/trades/clear-all', { method: 'DELETE' });
  await load();
}

async function manualBuy() {
  const inp = document.getElementById('manualMint');
  const btn = document.getElementById('manualBuyBtn');
  const statusEl = document.getElementById('manualBuyStatus');
  const mint = inp.value.trim();
  if (!mint) return;
  btn.disabled = true;
  btn.textContent = 'Buying…';
  statusEl.textContent = '';
  statusEl.style.color = '';
  try {
    const r = await fetch('/copytrade/api/trade/manual', {
      method: 'POST',
      headers: {'Content-Type':'application/json'},
      body: JSON.stringify({mint})
    });
    const data = await r.json();
    if (!r.ok || !data.ok) {
      alert('Buy failed: ' + (data?.msg || 'check Log tab'));
    } else {
      inp.value = '';
      statusEl.textContent = data.msg;
      statusEl.style.color = '#6bcb6b';
      await load();
    }
  } catch(e) {
    alert('Error: ' + e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = 'Buy Manually';
  }
}

load();
setInterval(load, 3000);
</script>
</body>
</html>
""";
}
