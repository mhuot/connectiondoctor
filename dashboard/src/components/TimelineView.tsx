import { useMemo } from 'react';
import type { ContractEnvelope, ContractEvent } from '../contract/types';
import { ROOT_EVENT_KINDS } from '../contract/types';
import { stitchIncidents } from '../domain/incidents';

/** Event-derived timeline: device-count steps, root-event rules, incidents.
 *  Charts step rather than slope — the link is up or down, and a slope implies
 *  states that never existed. */
export function TimelineView({ events, snapshot, recordedLabel }: {
  events: ContractEvent[];
  snapshot?: ContractEnvelope;
  recordedLabel: string;
}) {
  const incidents = useMemo(() => stitchIncidents(events, snapshot), [events, snapshot]);
  const series = useMemo(() => deviceCountSeries(events, snapshot), [events, snapshot]);
  const roots = events.filter((e) => ROOT_EVENT_KINDS.has(e.kind));

  if (events.length === 0) {
    return <p className="empty">No event stream loaded — drop a v1 events .jsonl to see the timeline.</p>;
  }

  const t0 = Date.parse(series[0].t);
  const t1 = Date.parse(series[series.length - 1].t) || t0 + 1;
  const W = 860, H = 140, PAD = 30;
  const x = (t: string) => PAD + ((Date.parse(t) - t0) / Math.max(1, t1 - t0)) * (W - 2 * PAD);
  const maxCount = Math.max(...series.map((s) => s.count), 1);
  const y = (c: number) => H - PAD - (c / maxCount) * (H - 2 * PAD);

  const path = series
    .flatMap((s, i) => {
      const px = x(s.t), py = y(s.count);
      if (i === 0) return [`M ${px} ${py}`];
      const prev = y(series[i - 1].count);
      return [`L ${px} ${prev}`, `L ${px} ${py}`]; // step interpolation
    })
    .join(' ');

  return (
    <div className="timeline">
      <div className="toolbar"><strong>Devices over time</strong><span className="spacer" /><span className="recorded">{recordedLabel}</span></div>
      <svg width={W} height={H}>
        {roots.map((e) => (
          <line key={e.t + e.kind} x1={x(e.t)} y1={PAD / 2} x2={x(e.t)} y2={H - PAD / 2}
            stroke="#e2574c" strokeDasharray="3 2" />
        ))}
        <path d={path} fill="none" stroke="#c678dd" strokeWidth={2} />
        <text x={PAD} y={12} fill="var(--muted)" fontSize={10}>device count (root events marked)</text>
      </svg>

      <h3>Incidents</h3>
      {incidents.length === 0 && <p className="empty">None in this stream.</p>}
      {incidents.map((inc) => (
        <div className="incident" key={inc.start}>
          <strong>{inc.start}</strong>
          <span>
            {inc.rootEvent ? `${inc.rootEvent} · ` : ''}
            {inc.eventCount} events
            {inc.devicesLost.length > 0 && ` · lost ${inc.devicesLost.length}: ${inc.devicesLost.map((d) => d.name).slice(0, 4).join(', ')}`}
          </span>
          {inc.sharedParent && (
            <em>all behind {inc.sharedParent.name} — one upstream failure, not {inc.devicesLost.length} device failures</em>
          )}
        </div>
      ))}
    </div>
  );
}

function deviceCountSeries(events: ContractEvent[], snapshot?: ContractEnvelope) {
  const sorted = [...events].sort((a, b) => Date.parse(a.t) - Date.parse(b.t));
  let count = snapshot?.nodes.filter((n) => n.kind === 'device' || n.kind === 'hub').length ?? 0;
  const out = [{ t: sorted[0]?.t ?? new Date().toISOString(), count }];
  for (const e of sorted) {
    if (e.kind === 'deviceAdded') count += 1;
    if (e.kind === 'deviceRemoved') count = Math.max(0, count - 1);
    if (e.kind === 'fullSnapshot' && e.snapshot) {
      count = e.snapshot.nodes.filter((n) => n.kind === 'device' || n.kind === 'hub').length;
    }
    out.push({ t: e.t, count });
  }
  return out;
}
