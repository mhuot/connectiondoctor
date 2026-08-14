/**
 * Incident stitching: raw events arrive in the hundreds during a single fault
 * and individually mean nothing. Events within GAP of the previous one are one
 * incident; root events (linkDown) name the origin; grouped removals attribute
 * to their deepest shared ancestor when a pre-incident snapshot is available.
 */

import {
  ROOT_EVENT_KINDS,
  type ContractEnvelope,
  type ContractEvent,
  type EventKind,
} from '../contract/types';

const GAP_MS = 30_000;

export interface Incident {
  start: string;
  end: string;
  eventCount: number;
  rootEvent?: EventKind;
  devicesLost: Array<{ vidPid?: string; name: string }>;
  sharedParent?: { id: string; name: string };
}

export function stitchIncidents(
  events: ContractEvent[],
  preIncidentSnapshot?: ContractEnvelope,
): Incident[] {
  const sorted = [...events]
    .filter((e) => e.kind !== 'fullSnapshot')
    .sort((a, b) => Date.parse(a.t) - Date.parse(b.t));
  if (sorted.length === 0) return [];

  const groups: ContractEvent[][] = [];
  let current: ContractEvent[] = [sorted[0]];
  for (const event of sorted.slice(1)) {
    const previous = current[current.length - 1];
    if (Date.parse(event.t) - Date.parse(previous.t) <= GAP_MS) current.push(event);
    else {
      groups.push(current);
      current = [event];
    }
  }
  groups.push(current);

  return groups
    .filter((g) => !(g.length === 1 && g[0].kind === 'adapterChanged')) // a lone plug event is not a fault
    .map((group) => {
      const lost = group
        .filter((e) => e.kind === 'deviceRemoved')
        .map((e) => ({ vidPid: e.vidPid, name: e.name ?? e.vidPid ?? e.nodeId ?? 'unknown' }));
      const incident: Incident = {
        start: group[0].t,
        end: group[group.length - 1].t,
        eventCount: group.length,
        rootEvent: group.find((e) => ROOT_EVENT_KINDS.has(e.kind))?.kind,
        devicesLost: lost,
      };
      if (preIncidentSnapshot && lost.length >= 2) {
        const parent = sharedParent(
          group.filter((e) => e.kind === 'deviceRemoved').map((e) => e.nodeId),
          preIncidentSnapshot,
        );
        if (parent) incident.sharedParent = parent;
      }
      return incident;
    });
}

/** Deepest common ancestor of the removed nodes in the pre-incident tree —
 *  the grouped-loss rule: one upstream failure, not N device failures. */
function sharedParent(
  nodeIds: Array<string | undefined>,
  snapshot: ContractEnvelope,
): { id: string; name: string } | undefined {
  const ids = nodeIds.filter((id): id is string => Boolean(id));
  if (ids.length < 2) return undefined;

  const byId = new Map(snapshot.nodes.map((n) => [n.id, n]));
  const chain = (id: string): string[] => {
    const out: string[] = [];
    let cur = byId.get(id)?.parentId;
    const seen = new Set<string>();
    while (cur && byId.has(cur) && !seen.has(cur)) {
      out.push(cur);
      seen.add(cur);
      cur = byId.get(cur)?.parentId;
    }
    return out; // nearest ancestor first
  };

  const chains = ids.map(chain);
  if (chains.some((c) => c.length === 0)) return undefined;
  const [first, ...rest] = chains;
  const common = first.find((ancestor) => rest.every((c) => c.includes(ancestor)));
  if (!common) return undefined;
  const node = byId.get(common);
  return node ? { id: node.id, name: node.name } : undefined;
}
