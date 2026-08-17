import type { ContractAnalysis, ContractFinding } from '../contract/types';

const SEVERITY_RANK = { critical: 0, warning: 1, info: 2 } as const;
const CONFIDENCE_RANK: Record<string, number> = { 'very high': 0, high: 1, moderate: 2 };

/** Ranked findings with the evidence that produced them. Every finding shows
 *  its evidence without interaction — a verdict you cannot audit is an opinion.
 *  "None" is only claimed when the recording can vouch for the window. */
export function FindingsView({ findings, analysis, hostName }: {
  findings?: ContractFinding[];
  analysis?: ContractAnalysis;
  hostName?: string;
}) {
  if (!analysis) {
    return (
      <div className="findings">
        <p className="empty">
          No recording on {hostName ?? 'this collector'} yet — findings need history.
          Run <code>install</code> so it records at login, or <code>collect</code> in a terminal.
        </p>
      </div>
    );
  }

  const ranked = [...(findings ?? [])].sort((a, b) =>
    SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] ||
    (CONFIDENCE_RANK[a.confidence ?? ''] ?? 9) - (CONFIDENCE_RANK[b.confidence ?? ''] ?? 9) ||
    a.title.localeCompare(b.title));

  const cov = analysis.coverage;
  const window = `last ${analysis.windowHours} h · generated ${fmt(analysis.generatedAt)}`;

  return (
    <div className="findings">
      <div className="toolbar">
        <strong>Findings</strong>
        <span className="recorded">{window}</span>
        <span className="spacer" />
        <span className={`chip ${cov.complete ? 'ok' : 'warn'}`}>
          {cov.complete ? 'window complete' : `history incomplete: ${(cov.reasons ?? ['unknown']).join(', ')}`}
        </span>
        {analysis.baseline && (
          <span className={`chip ${baselineTone(analysis.baseline.state)}`}>baseline: {analysis.baseline.state}</span>
        )}
      </div>

      {ranked.length === 0 && (
        cov.complete
          ? <p className="empty">No findings in the last {analysis.windowHours} h — the recording covers the whole window.</p>
          : <p className="empty">Unknown — history is incomplete ({(cov.reasons ?? []).join(', ') || 'no reason given'}), covering {fmt(cov.availableFrom)} → {fmt(cov.through)}. "No findings" cannot be claimed for this window.</p>
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
