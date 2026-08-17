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
/** A deficit long enough to be worth a mark on the timeline. The event log
 *  records any dip past -2 W so the record is honest; a *finding* needs 10 W
 *  sustained. Between those sits the question this constant answers: how long
 *  must the supply be short before someone looking at the timeline should see
 *  it at all. A five-second dip on a laptop at full charge is noise; a minute
 *  is the supply failing to keep up. */
const SUSTAINED_DEFICIT_MS = 60_000;

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

  // A deficit is one episode from its start to its end, however long that is.
  // Grouping purely by gaps would split a two-minute deficit into two runs of
  // one event each — and then neither run looks sustained, so the longer the
  // fault, the quieter the timeline. Events inside an open episode stay with
  // it regardless of the gap.
  const openEpisodeAt = (index: number): boolean => {
    let open = false;
    for (let i = 0; i <= index; i++) {
      if (sorted[i].kind === 'deficitStart') open = true;
      if (sorted[i].kind === 'deficitEnd') open = false;
    }
    return open;
  };

  const groups: ContractEvent[][] = [];
  let current: ContractEvent[] = [sorted[0]];
  for (let i = 1; i < sorted.length; i++) {
    const event = sorted[i];
    const previous = current[current.length - 1];
    const withinEpisode = openEpisodeAt(i - 1);
    if (withinEpisode || Date.parse(event.t) - Date.parse(previous.t) <= GAP_MS) current.push(event);
    else {
      groups.push(current);
      current = [event];
    }
  }
  groups.push(current);

  return groups
    // An incident is a run of *trouble*. Trouble is something lost — a device
    // disappearing, a link dropping, a port erroring — or a deficit that
    // persisted: the supply failing to keep up is a fault even when nothing
    // dropped off, and the timeline is where someone looks to see when it
    // happened. A run made only of arrivals, plug events or a momentary dip is
    // a desk being used, and reporting it is the false alarm that teaches
    // people to ignore the timeline. (The controls in docs/fixtures hold this
    // line from both sides: a five-second dip is silent, two minutes is not.)
    .filter((g) => g.some((e) => e.kind === 'deviceRemoved' || ROOT_EVENT_KINDS.has(e.kind)) || sustainedDeficit(g))
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

/** True when this run contains a deficit that lasted long enough to matter.
 *  An episode still open at the end of the run counts from its start to the
 *  last event we have: an unfinished deficit is not a reason to stay quiet. */
function sustainedDeficit(group: ContractEvent[]): boolean {
  let start: number | undefined;
  for (const event of group) {
    if (event.kind === 'deficitStart') start ??= Date.parse(event.t);
    if (event.kind === 'deficitEnd' && start !== undefined) {
      if (Date.parse(event.t) - start >= SUSTAINED_DEFICIT_MS) return true;
      start = undefined;
    }
  }
  if (start === undefined) return false;
  return Date.parse(group[group.length - 1].t) - start >= SUSTAINED_DEFICIT_MS;
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
