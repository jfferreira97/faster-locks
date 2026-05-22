using FasterAlerts.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace FasterAlerts.Controllers;

[ApiController]
[Route("heatmap")]
public class HeatmapController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ContentResult> Get()
    {
        var all    = (await db.SentAlerts.ToListAsync()).Select(a => new { a.Id, a.TokenSymbol, a.MarketCapUsd, a.PercentSupply, Local = a.SentAt.ToLocalTime() }).ToList();
        var cutoff = DateTimeOffset.Now.AddDays(-7);
        var recent = all.Where(a => a.Local >= cutoff).ToHashSet();

        // [day 0-6][hour 0-23]
        var counts = new int[7, 24];
        var tips   = new List<string>[7, 24];
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
                tips[d, h] = [];

        foreach (var a in all)
        {
            var d = (int)a.Local.DayOfWeek;
            var h = a.Local.Hour;
            counts[d, h]++;
            if (recent.Contains(a))
                tips[d, h].Add($"#{a.Id} ${a.TokenSymbol} | {FormatMc(a.MarketCapUsd)} MC | {a.PercentSupply:F2}% supply | {a.Local:HH:mm}");
        }

        // weeks elapsed from first alert to now (min 1 so we never divide by zero)
        var numWeeks = all.Any()
            ? Math.Max(1.0, (DateTimeOffset.Now - all.Min(a => a.Local)).TotalDays / 7.0)
            : 1.0;

        // day = column (X), hour = row (Y); v = avg per week (1 decimal)
        var dataPoints = new List<object>();
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
            {
                var avg = Math.Round(counts[d, h] / numWeeks, 1);
                dataPoints.Add(new { day = d, hour = h, v = avg, tips = tips[d, h].ToArray() });
            }

        var json    = JsonSerializer.Serialize(dataPoints);
        var total   = all.Count;
        var updated = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm");

        var html = new StringBuilder();
        html.Append($$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>FASTER Locks — Heatmap</title>
<script src="https://d3js.org/d3.v7.min.js"></script>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  body {
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
    background: #0e1621;
    color: #fff;
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 32px 16px;
  }
  .wrap { width: 100%; max-width: 920px; }
  .header {
    background: #17212b;
    border-radius: 12px 12px 0 0;
    padding: 14px 20px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    border-bottom: 1px solid #0e1621;
  }
  .header-title { font-size: 15px; font-weight: 500; }
  .header-sub   { font-size: 12px; color: #6c7883; margin-top: 2px; }
  .header-right { font-size: 12px; color: #6c7883; text-align: right; }
  .body { background: #17212b; border-radius: 0 0 12px 12px; padding: 20px; overflow-x: auto; }
  #chart { line-height: 0; }
  .legend {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 14px;
    justify-content: flex-end;
  }
  .legend-label { font-size: 11px; color: #6c7883; }
  #lgnd { border-radius: 3px; display: block; }
  #tip {
    display: none;
    position: fixed;
    background: #17212b;
    border: 1px solid #2b5278;
    border-radius: 8px;
    padding: 10px 14px;
    font-size: 12px;
    color: #e8e9ea;
    max-width: 360px;
    z-index: 999;
    pointer-events: none;
    line-height: 1.9;
  }
  .tip-hdr  { color: #6c7883; font-size: 11px; margin-bottom: 4px; }
  .tip-row  { color: #5ca8e2; }
  .tip-none { color: #6c7883; font-style: italic; }
</style>
</head>
<body>
<div class="wrap">
  <div class="header">
    <div>
      <div class="header-title">🔒 Lock Frequency Heatmap</div>
      <div class="header-sub">Avg locks/week per slot · tooltip shows last 7 days</div>
    </div>
    <div class="header-right">{{total}} alerts<br>{{updated}} UTC</div>
  </div>
  <div class="body">
    <div id="chart"></div>
    <div class="legend">
      <span class="legend-label">low</span>
      <canvas id="lgnd" width="120" height="10"></canvas>
      <span class="legend-label">high</span>
    </div>
  </div>
</div>
<div id="tip"></div>

<script>
const DAYS  = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
const HOURS = Array.from({length:24}, (_,i) => String(i).padStart(2,'0') + ':00');
const DATA  = {{json}};
const tip   = document.getElementById('tip');

const margin = {top:16, right:16, bottom:46, left:60};
const cellW  = 106;
const cellH  = 19;
const W = cellW * 7;
const H = cellH * 24;
const svgW = W + margin.left + margin.right;
const svgH = H + margin.top  + margin.bottom;

const svg = d3.select('#chart')
  .append('svg')
  .attr('viewBox', `0 0 ${svgW} ${svgH}`)
  .attr('width', '100%')
  .append('g')
  .attr('transform', `translate(${margin.left},${margin.top})`);

const x = d3.scaleBand().domain(d3.range(7)).range([0, W]).paddingInner(0.08).paddingOuter(0.02);
const y = d3.scaleBand().domain(d3.range(24)).range([0, H]).paddingInner(0.08).paddingOuter(0.02);

const maxV  = d3.max(DATA, d => d.v) || 1;
const color = d3.scaleSequential(d3.interpolateYlOrRd).domain([0, maxV]);

// Cells
svg.selectAll('rect.cell')
  .data(DATA)
  .enter().append('rect')
  .attr('class', 'cell')
  .attr('x', d => x(d.day))
  .attr('y', d => y(d.hour))
  .attr('width', x.bandwidth())
  .attr('height', y.bandwidth())
  .attr('rx', 3)
  .attr('fill', d => d.v === 0 ? '#0d1a27' : color(d.v))
  .on('mousemove', function(evt, d) {
    const avg  = d.v >= 10 ? Math.round(d.v) : d.v.toFixed(1);
    const hdr  = DAYS[d.day] + ' ' + HOURS[d.hour] + ' — ' + avg + ' locks/week avg';
    const rows = d.tips.length
      ? d.tips.map(r => '<div class="tip-row">• ' + r + '</div>').join('')
      : '<div class="tip-none">no locks in last 7 days</div>';
    tip.innerHTML = '<div class="tip-hdr">' + hdr + '</div>' + rows;
    tip.style.display = 'block';
    tip.style.left = (evt.clientX + 16) + 'px';
    tip.style.top  = (evt.clientY + 16) + 'px';
  })
  .on('mouseleave', function() { tip.style.display = 'none'; });

// Cell labels
svg.selectAll('text.label')
  .data(DATA.filter(d => d.v > 0))
  .enter().append('text')
  .attr('class', 'label')
  .attr('x', d => x(d.day) + x.bandwidth() / 2)
  .attr('y', d => y(d.hour) + y.bandwidth() / 2)
  .attr('text-anchor', 'middle')
  .attr('dominant-baseline', 'central')
  .style('font-size', '9px')
  .style('font-weight', '600')
  .style('pointer-events', 'none')
  .style('fill', d => (d.v / maxV) > 0.55 ? '#fff' : '#1a1a1a')
  .text(d => d.v >= 10 ? Math.round(d.v) : d.v.toFixed(1));

// X axis — weekdays along the bottom
svg.append('g')
  .attr('transform', `translate(0,${H})`)
  .call(
    d3.axisBottom(x)
      .tickFormat(i => DAYS[i])
      .tickSize(0)
  )
  .call(g => g.select('.domain').remove())
  .selectAll('text')
  .attr('y', 12)
  .style('fill', '#c5cdd6')
  .style('font-size', '13px')
  .style('font-weight', '600')
  .style('font-family', 'inherit')
  .style('text-anchor', 'middle');

// Y axis — hour spans on the left
svg.append('g')
  .call(
    d3.axisLeft(y)
      .tickFormat(i => HOURS[i])
      .tickSize(0)
  )
  .call(g => g.select('.domain').remove())
  .selectAll('text')
  .attr('x', -10)
  .style('fill', '#8a96a0')
  .style('font-size', '11px')
  .style('font-family', 'inherit')
  .style('text-anchor', 'end');

// Legend
const lc = document.getElementById('lgnd').getContext('2d');
for (let i = 0; i < 120; i++) {
  lc.fillStyle = color((i / 119) * maxV);
  lc.fillRect(i, 0, 1, 10);
}
</script>
</body>
</html>
""");

        return Content(html.ToString(), "text/html");
    }

    private static string FormatMc(decimal mc) => mc switch
    {
        >= 1_000_000_000 => $"{mc / 1_000_000_000:0.##}b",
        >= 1_000_000     => $"{mc / 1_000_000:0.##}m",
        _                => $"{mc / 1_000:0.##}k"
    };
}
