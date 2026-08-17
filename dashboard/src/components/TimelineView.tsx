import { useMemo } from 'react';
import type { ContractEnvelope, ContractEvent } from '../contract/types';
import { ROOT_EVENT_KINDS } from '../contract/types';
import { stitchIncidents, type Incident } from '../domain/incidents';

/** Event-derived timeline: device-count steps, root-event rules, incidents.
 *  Charts step rather than slope — the link is up or down, and a slope implies
 *  states that never existed. */
export function TimelineView({ events, snapshot, recordedLabel }: {
  events: ContractEvent[];
  snapshot?: ContractEnvelope;
  recordedLabel: string;
}) {
  // Producer incidents win when the envelope carries them (the collector saw
  // the raw samples; we only see events); otherwise stitch our own, and say
  // which — the two are not the same evidence.
  const fromCollector = snapshot?.incidents;
  const incidents = useMemo<Incident[]>(() => fromCollector
    ? fromCollector.map((inc) => ({
        start: inc.start, end: inc.end ?? inc.start, eventCount: 0, rootEvent: inc.rootEvent,
        devicesLost: inc.devicesLost ?? [],
        sharedParent: inc.sharedParent
          ? { id: inc.sharedParent, name: snapshot?.nodes.find((n) => n.id === inc.sharedParent)?.name ?? inc.sharedParent }
          : undefined,
      }))
    : stitchIncidents(events, snapshot), [events, snapshot, fromCollector]);
  const incidentSource = fromCollector ? 'incidents from collector' : 'incidents derived by dashboard';
  const series = useMemo(() => deviceCountSeries(events, snapshot), [events, snapshot]);
  const roots = events.filter((e) => ROOT_EVENT_KINDS.has(e.kind));

  if (events.length === 0 && !fromCollector) {
    return <p className="empty">No event stream loaded — drop a v1 events .jsonl to see the timeline.</p>;
  }

  const t0 = Date.parse(series[0]?.t ?? new Date().toISOString());
  const t1 = Date.parse(series[series.length - 1]?.t ?? '') || t0 + 1;
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
      {events.length > 0 && <svg width={W} height={H}>
        {roots.map((e) => (
          <line key={e.t + e.kind} x1={x(e.t)} y1={PAD / 2} x2={x(e.t)} y2={H - PAD / 2}
            stroke="#e2574c" strokeDasharray="3 2" />
        ))}
        <path d={path} fill="none" stroke="#c678dd" strokeWidth={2} />
        <text x={PAD} y={12} fill="var(--muted)" fontSize={10}>device count (root events marked)</text>
      </svg>}

      <h3>Incidents <span className="muted">· {incidentSource}</span></h3>
      {incidents.length === 0 && (
        snapshot?.analysis && !snapshot.analysis.coverage.complete
          ? <p className="empty">Unknown — history incomplete ({(snapshot.analysis.coverage.reasons ?? []).join(', ')}).</p>
          : <p className="empty">None in this stream.</p>
      )}
      {incidents.map((inc) => (
        <div className="incident" key={inc.start}>
          <strong>{inc.start}</strong>
          <span>
            {inc.rootEvent ? `${inc.rootEvent} · ` : ''}
            {inc.eventCount > 0 ? `${inc.eventCount} events` : (inc.end !== inc.start ? `until ${inc.end}` : 'single event')}
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
