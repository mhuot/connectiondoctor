import type { ContractAnalysis, ContractFinding } from '../contract/types';
import { useState } from 'react';
import type { HistoryState, HostData } from '../data/store';
import { canMutate, captureBaseline } from '../data/baseline';

const SEVERITY_RANK = { critical: 0, warning: 1, info: 2 } as const;
const CONFIDENCE_RANK: Record<string, number> = { 'very high': 0, high: 1, moderate: 2 };

/** Ranked findings with the evidence that produced them. Every finding shows
 *  its evidence without interaction — a verdict you cannot audit is an opinion.
 *  "None" is only claimed when the recording can vouch for the window. */
export function FindingsView({ findings, analysis, hostName, eventCount = 0, lastEventAt, history, host, onChanged }: {
  findings?: ContractFinding[];
  analysis?: ContractAnalysis;
  hostName?: string;
  /** Recorded events loaded for this host, so an absent `analysis` can be
   *  told apart from an empty machine (issue #36). */
  eventCount?: number;
  lastEventAt?: string;
  /** Per-host history quality (durable reasons, events-fetch state). When it
   *  says incomplete, "no findings" is not a claim this panel may make even
   *  if the producer's own coverage looked complete at the time. */
  history?: { state: HistoryState; reasons: string[] };
  /** The active host, for the baseline control (only offered on the collector
   *  serving this page — mutations are loopback and same-origin). */
  host?: HostData;
  onChanged?: () => void;
}) {
  if (!analysis) {
    // Absent analysis means the collector did not report any — which is
    // "never recorded" only when there is also no history to be seen. A
    // producer that has events on file but does not emit analysis yet (the
    // Windows collector, until its producer slice lands) must not read as
    // a machine with nothing to say.
    return (
      <div className="findings">
        {host && (
          <div className="toolbar">
            <strong>Baseline</strong>
            <BaselineControl host={host} baseline={undefined} onChanged={onChanged} />
          </div>
        )}
        {eventCount > 0 ? (
          <p className="empty">
            {hostName ?? 'This collector'} reported no analysis — its collector does not emit
            findings yet — although it has {eventCount} recorded event{eventCount === 1 ? '' : 's'} on file
            {lastEventAt ? `, the last at ${fmt(lastEventAt)}` : ''}. Findings, incidents and baseline state
            are <b>unknown</b> for this host, not clear.
          </p>
        ) : (
          <p className="empty">
            No recording on {hostName ?? 'this collector'} yet — findings need history.
            Run <code>install</code> so it records at login, or <code>collect</code> in a terminal.
          </p>
        )}
      </div>
    );
  }

  const ranked = [...(findings ?? [])].sort((a, b) =>
    SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] ||
    (CONFIDENCE_RANK[a.confidence ?? ''] ?? 9) - (CONFIDENCE_RANK[b.confidence ?? ''] ?? 9) ||
    a.title.localeCompare(b.title));

  const cov = analysis.coverage;
  const window = `last ${analysis.windowHours} h · generated ${fmt(analysis.generatedAt)}`;
  const historyOk = cov.complete && (history === undefined || history.state === 'complete');
  // The baseline is a separate question from the recording: it can be unknown
  // (being replaced, unreadable) while the history is complete. Either one
  // being unknown means "no findings" is not a claim this panel may make.
  const baselineAvailability = analysis.capabilities?.baseline ?? 'available';
  const baselineKnown = baselineAvailability === 'available';
  const whyIncomplete = [...(cov.complete ? [] : (cov.reasons ?? ['unknown'])), ...(history?.reasons ?? [])];

  return (
    <div className="findings">
      <div className="toolbar">
        <strong>Findings</strong>
        <span className="recorded">{window}</span>
        <span className="spacer" />
        <span className={`chip ${historyOk ? 'ok' : 'warn'}`} role="status" aria-live="polite">
          {historyOk ? 'window complete' : `history incomplete: ${[...new Set(whyIncomplete)].join(', ')}`}
        </span>
        {!baselineKnown && (
          <span className="chip warn" role="status" aria-live="polite">
            baseline unknown ({baselineAvailability.replace(/-/g, ' ')})
          </span>
        )}
        {analysis.baseline && (
          <span className={`chip ${baselineTone(analysis.baseline.state)}`} role="status" aria-live="polite">
            baseline: {analysis.baseline.state}
            {analysis.baseline.state === 'recovered' && analysis.baseline.recoveredAt ? ` at ${fmt(analysis.baseline.recoveredAt)}` : ''}
          </span>
        )}
        {host && <BaselineControl host={host} baseline={analysis.baseline} onChanged={onChanged} />}
      </div>

      {findings === undefined && (
        <p className="empty">This collector reported analysis but no <code>findings</code> field — findings are <b>not reported</b> for this host (unknown, not none), even though the window is {historyOk ? 'complete' : 'incomplete'}.</p>
      )}
      {/* An incomplete window explains what is *unknown*; any findings above
          are what is *known right now* (live power, baseline comparison), and
          they are rendered regardless — a stale recorder must never hide the
          fault in front of the user. */}
      {findings !== undefined && ranked.length === 0 && !baselineKnown && (
        <p className="empty">
          Unknown — the known-good baseline could not be evaluated ({baselineAvailability.replace(/-/g, ' ')}),
          so a comparison against it was not made. {historyOk ? 'Recorded history is complete for this window.' : ''}
        </p>
      )}
      {findings !== undefined && ranked.length === 0 && baselineKnown && (
        historyOk
          ? <p className="empty">No findings in the last {analysis.windowHours} h — the recording covers the whole window.</p>
          : <p className="empty">Unknown — history is incomplete ({[...new Set(whyIncomplete)].join(', ') || 'no reason given'}), covering {fmt(cov.availableFrom)} → {fmt(cov.through)}. "No findings" cannot be claimed for this window.</p>
      )}

      {ranked.map((f, i) => (
        <article className={`finding ${f.severity}`} key={`${f.title}-${i}`}>
          <header>
            <span className={`sev ${f.severity}`}>{f.severity}</span>
            <strong>{f.title}</strong>
            {f.confidence && <span className="muted">confidence: {f.confidence}</span>}
          </header>
          <p>{f.explanation}</p>
          <ul className="evidence">
            {f.evidence.map((e, j) => <li key={j}><code>{e}</code></li>)}
          </ul>
          {f.recommendation && <p className="recommendation"><b>Action:</b> {f.recommendation}</p>}
        </article>
      ))}
    </div>
  );
}

function baselineTone(state: string): string {
  return state === 'active-fault' ? 'crit' : state === 'healthy' ? 'ok' : state === 'recovered' ? 'warn' : 'muted';
}

function fmt(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { hour12: false });
}

/** Capture / replace the known-good baseline. Only shown for the collector
 *  serving this page; replacement is destructive, so it names the capture time
 *  it is about to discard and sends that time as If-Match — if another tab
 *  already replaced it, the server refuses and we say so. */
function BaselineControl({ host, baseline, onChanged }: {
  host: HostData;
  baseline?: { state: string; capturedAt?: string };
  onChanged?: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  if (!canMutate(host)) {
    return <span className="muted" title="Mutations are loopback and same-origin only (docs/embedding.md § Mutations)">baseline actions: on the collector's own page only</span>;
  }

  const run = async (replace: boolean) => {
    if (replace && baseline?.capturedAt &&
      !window.confirm(`Replace the known-good baseline captured ${fmt(baseline.capturedAt)}? The old one is discarded.`)) return;
    setBusy(true);
    const result = await captureBaseline(host, { replace, ifMatch: replace ? baseline?.capturedAt : undefined });
    setBusy(false);
    setMessage(result.ok
      ? `Baseline ${result.replaced ? 'replaced' : 'captured'} at ${fmt(result.capturedAt ?? '')}`
      : result.error === 'stale'
        ? `Not replaced — another tab already saved one at ${fmt(result.current?.capturedAt ?? '')}. Refresh and try again.`
        : result.error === 'read-only-binding'
          ? 'Refused: this collector is bound to the LAN, where it stays read-only.'
          : `Not saved: ${result.error}`);
    if (result.ok) onChanged?.();
  };

  return (
    <>
      <button disabled={busy} onClick={() => void run(Boolean(baseline?.capturedAt))}>
        {baseline?.capturedAt ? 'Replace baseline…' : 'Capture baseline'}
      </button>
      {message && <span className="muted" role="status" aria-live="polite">{message}</span>}
    </>
  );
}
