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
  currentSnapshot?: ContractEnvelope,
): Incident[] {
  const ordered = [...events].sort((a, b) => Date.parse(a.t) - Date.parse(b.t));
  // Sync points, kept aside: attributing a grouped loss needs the topology as
  // it was *before* the devices vanished. The current envelope is "now" — the
  // devices in question are exactly the ones missing from it — so a
  // fullSnapshot recorded before the incident is the only evidence that can
  // name their shared parent. The current envelope is the fallback for streams
  // that carry no sync point.
  const snapshots = ordered.filter(
    (e): e is ContractEvent & { snapshot: ContractEnvelope } =>
      e.kind === 'fullSnapshot' && e.snapshot !== undefined,
  );
  const topologyBefore = (t: string): ContractEnvelope | undefined => {
    const at = Date.parse(t);
    for (let i = snapshots.length - 1; i >= 0; i--) {
      if (Date.parse(snapshots[i].t) <= at) return snapshots[i].snapshot;
    }
    return currentSnapshot;
  };

  const sorted = ordered.filter((e) => e.kind !== 'fullSnapshot');
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
    // An incident is a run of *trouble*, and trouble means something was lost:
    // a device disappearing, a link dropping, a port erroring. A run made only
    // of arrivals, power transitions or plug events is a desk being used —
    // reporting it as an incident is the false alarm that teaches people to
    // ignore the timeline. (The conformance controls in docs/fixtures exist to
    // hold this line: normal unplug, shallow deficit, a device arriving.)
    .filter((g) => g.some((e) => e.kind === 'deviceRemoved' || ROOT_EVENT_KINDS.has(e.kind)))
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
      const before = topologyBefore(group[0].t);
      if (before && lost.length >= 2) {
        const parent = sharedParent(
          group.filter((e) => e.kind === 'deviceRemoved').map((e) => e.nodeId),
          before,
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

  // When one of the lost devices is itself the ancestor of all the others,
  // *it* is what failed — the rest are its downstream fallout, and naming its
  // parent instead would send someone to investigate the dock when the hub
  // hanging off it is the thing that stopped enumerating.
  const lost = new Set(ids);
  const ancestorOfTheRest = ids.find((candidate) =>
    ids.every((other) => other === candidate || chain(other).includes(candidate)));
  if (ancestorOfTheRest) {
    const node = byId.get(ancestorOfTheRest);
    if (node) return { id: node.id, name: node.name };
  }

  // Otherwise: the deepest ancestor common to everything that was lost, and
  // not itself among the losses.
  const chains = ids.map(chain);
  if (chains.some((c) => c.length === 0)) return undefined;
  const [first, ...rest] = chains;
  const common = first.find((ancestor) => !lost.has(ancestor) && rest.every((c) => c.includes(ancestor)));
  if (!common) return undefined;
  const node = byId.get(common);
  return node ? { id: node.id, name: node.name } : undefined;
}
