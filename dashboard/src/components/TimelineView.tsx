import { useMemo } from 'react';
import type { ContractEnvelope, ContractEvent } from '../contract/types';
import { ROOT_EVENT_KINDS } from '../contract/types';
import { stitchIncidents, type Incident } from '../domain/incidents';
import { deviceCountSeries } from '../domain/series';
import type { HistoryState } from '../data/store';

/** Event-derived timeline: device-count steps, root-event rules, incidents.
 *  Charts step rather than slope — the link is up or down, and a slope implies
 *  states that never existed. */
export function TimelineView({ events, snapshot, recordedLabel, history, eventsError }: {
  events: ContractEvent[];
  snapshot?: ContractEnvelope;
  recordedLabel: string;
  history?: { state: HistoryState; reasons: string[] };
  eventsError?: string;
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

  if (series.contradiction) {
    return <p className="empty">Device count unknown — {series.contradiction}. The topology view still shows what is attached now.</p>;
  }
  if (eventsError && events.length === 0) {
    return <p className="empty">History unavailable — the events stream could not be fetched ({eventsError}). Nothing here can be called quiet.</p>;
  }
  if (events.length === 0 && !fromCollector) {
    return <p className="empty">No event stream loaded — drop a v1 events .jsonl to see the timeline.</p>;
  }
  const incomplete = history && history.state !== 'complete';

  const points = series.points;
  const t0 = Date.parse(points[0]?.t ?? new Date().toISOString());
  const t1 = Date.parse(points[points.length - 1]?.t ?? '') || t0 + 1;
  const W = 860, H = 140, PAD = 30;
  const x = (t: string) => PAD + ((Date.parse(t) - t0) / Math.max(1, t1 - t0)) * (W - 2 * PAD);
  const maxCount = Math.max(...points.map((s) => s.count), 1);
  const y = (c: number) => H - PAD - (c / maxCount) * (H - 2 * PAD);

  const path = points
    .flatMap((s, i) => {
      const px = x(s.t), py = y(s.count);
      if (i === 0) return [`M ${px} ${py}`];
      const prev = y(points[i - 1].count);
      return [`L ${px} ${prev}`, `L ${px} ${py}`]; // step interpolation
    })
    .join(' ');

  return (
    <div className="timeline">
      <div className="toolbar"><strong>Devices over time</strong>
        {series.anchoredAt && <span className="chip muted">anchored at snapshot {series.anchoredAt}</span>}
        {eventsError && <span className="chip warn" role="status">events stale: {eventsError}</span>}
        <span className="spacer" /><span className="recorded">{recordedLabel}</span></div>
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
        incomplete || (snapshot?.analysis && !snapshot.analysis.coverage.complete)
          ? <p className="empty">Unknown — history incomplete ({[...(history?.reasons ?? []), ...(snapshot?.analysis?.coverage.reasons ?? [])].filter((r, i, a) => a.indexOf(r) === i).join(', ') || 'no reason given'}).</p>
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
          {inc.deficit && (
            <em>
              {inc.deficit.durationProven
                ? (inc.deficit.until
                    ? `power deficit from ${inc.deficit.since} to ${inc.deficit.until}`
                    : `power deficit since ${inc.deficit.since}, never reported resolved`)
                /* An unlocated gap could hold the missing end, or an end and a
                   later restart, so the transitions are evidence and the span
                   between them is not: saying "short of power for three hours"
                   over a hole in the recording would be inventing the hours.
                   "In this window" rather than "since" because nothing here
                   locates the incompleteness at the start. */
                : (inc.deficit.until
                    ? `power deficit recorded ${inc.deficit.since} → ${inc.deficit.until}; history incomplete in this window, so the duration between them is unproven`
                    : `power deficit began ${inc.deficit.since}; history incomplete in this window, so whether it resolved is unknown`)}
            </em>
          )}
          {inc.sharedParent && (
            <em>all behind {inc.sharedParent.name} — one upstream failure, not {inc.devicesLost.length} device failures</em>
          )}
        </div>
      ))}
    </div>
  );
}
