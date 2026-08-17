import type { ContractEnvelope, ContractEvent } from '../contract/types';

export interface CountPoint { t: string; count: number }
export interface CountSeries {
  points: CountPoint[];
  /** ISO time of the fullSnapshot the series is anchored on, when one exists. */
  anchoredAt?: string;
  /** Set when the series cannot be drawn honestly: no anchor, and the deltas
   *  contradict the current envelope (more removals than devices that exist).
   *  Callers show "unknown" rather than a line that disagrees with the tree. */
  contradiction?: string;
}

const countable = (env: ContractEnvelope): number =>
  env.nodes.filter((n) => n.kind === 'device' || n.kind === 'hub').length;

/** Device count over time, anchored on the last fullSnapshot: that sync point
 *  is a complete envelope, so counting starts from it and only later deltas
 *  are applied. Without one, the series is inferred backwards from the current
 *  envelope (count at start = current − net change), so it still ends where
 *  the current envelope says — never starting from "now" and replaying the
 *  whole past forwards, which double-counts history against the present. */
export function deviceCountSeries(events: ContractEvent[], snapshot?: ContractEnvelope): CountSeries {
  const sorted = [...events].sort((a, b) => Date.parse(a.t) - Date.parse(b.t));
  let anchor = -1;
  for (let i = sorted.length - 1; i >= 0; i--) {
    if (sorted[i].kind === 'fullSnapshot' && sorted[i].snapshot) { anchor = i; break; }
  }
  const delta = (e: ContractEvent): number => (e.kind === 'deviceAdded' ? 1 : e.kind === 'deviceRemoved' ? -1 : 0);

  if (anchor >= 0) {
    let count = countable(sorted[anchor].snapshot!);
    const points: CountPoint[] = [{ t: sorted[anchor].t, count }];
    for (const e of sorted.slice(anchor + 1)) {
      if (e.kind === 'fullSnapshot' && e.snapshot) count = countable(e.snapshot);
      else count = Math.max(0, count + delta(e));
      points.push({ t: e.t, count });
    }
    return { points, anchoredAt: sorted[anchor].t };
  }

  const net = sorted.reduce((s, e) => s + delta(e), 0);
  const current = snapshot ? countable(snapshot) : 0;
  const inferredStart = current - net;
  if (inferredStart < 0) {
    // The events say more devices left than the current envelope can account
    // for: history is missing (trimmed, or fetched from a different window).
    // Clamping and replaying would draw a line whose endpoint contradicts the
    // topology on screen, so say unknown instead.
    return {
      points: [],
      contradiction: `events imply ${-inferredStart} more device${inferredStart === -1 ? '' : 's'} than the current state has — history is incomplete`,
    };
  }

  let count = inferredStart;
  const points: CountPoint[] = [{ t: sorted[0]?.t ?? new Date().toISOString(), count }];
  for (const e of sorted) {
    count = Math.max(0, count + delta(e));
    points.push({ t: e.t, count });
  }
  return { points };
}
