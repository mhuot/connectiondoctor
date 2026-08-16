/**
 * Cross-host device-migration detection — the fleet view's core.
 *
 * A deviceRemoved(vidPid) on host A followed within the window by a
 * deviceAdded(same vidPid) on host B is a migration. Multiple devices moving
 * A→B in one window collapse into a single branch migration (the KVM case:
 * hub + keyboard + mouse as one arrow, not four). Duplicate hardware is
 * handled by count-matching: each remove consumes at most one add, and the
 * remove must precede the add — anything unmatched stays an independent event.
 */

import type { ContractEvent } from '../contract/types';

export const MIGRATION_WINDOW_MS = 120_000;
/** Removals within this span on one host are one departure group. */
const GROUP_SPAN_MS = 30_000;

export interface HostEvents {
  host: string;
  events: ContractEvent[];
}

export interface MigratedDevice {
  vidPid: string;
  name: string;
}

export interface Migration {
  from: string;
  to: string;
  /** Timestamp of the first removal in the group. */
  at: string;
  devices: MigratedDevice[];
  /** Set when a migrated device is a hub (usbClass 9 upstream knowledge is not
   *  in events, so callers may pass known hub vidPids). */
  branchRoot?: MigratedDevice;
}

export interface MigrationResult {
  migrations: Migration[];
  /** Removes with no matching add — genuinely gone, not migrated. */
  unmatchedRemovals: Array<{ host: string; event: ContractEvent }>;
  /** Adds with no matching remove — genuinely new on that host. */
  unmatchedAdds: Array<{ host: string; event: ContractEvent }>;
}

export function detectMigrations(
  streams: HostEvents[],
  options: { windowMs?: number; hubVidPids?: ReadonlySet<string> } = {},
): MigrationResult {
  const windowMs = options.windowMs ?? MIGRATION_WINDOW_MS;
  const hubVidPids = options.hubVidPids ?? new Set<string>();

  interface Tagged {
    host: string;
    event: ContractEvent;
    time: number;
    matched: boolean;
  }
  const removes: Tagged[] = [];
  const adds: Tagged[] = [];

  for (const { host, events } of streams) {
    for (const event of events) {
      if (!event.vidPid) continue;
      const time = Date.parse(event.t);
      if (Number.isNaN(time)) continue;
      if (event.kind === 'deviceRemoved') removes.push({ host, event, time, matched: false });
      if (event.kind === 'deviceAdded') adds.push({ host, event, time, matched: false });
    }
  }
  removes.sort((a, b) => a.time - b.time);
  adds.sort((a, b) => a.time - b.time);

  // Count-matched pairing: earliest eligible add per remove, remove-first only.
  const pairs: Array<{ remove: Tagged; add: Tagged }> = [];
  for (const remove of removes) {
    const add = adds.find(
      (a) =>
        !a.matched &&
        a.event.vidPid === remove.event.vidPid &&
        a.host !== remove.host &&
        a.time >= remove.time &&
        a.time - remove.time <= windowMs,
    );
    if (add) {
      add.matched = true;
      remove.matched = true;
      pairs.push({ remove, add });
    }
  }

  // Collapse pairs sharing (from, to) whose removals fall in one departure
  // group into a single branch migration.
  const migrations: Migration[] = [];
  const grouped = new Map<string, Array<{ remove: Tagged; add: Tagged }>>();
  for (const pair of pairs) {
    const key = `${pair.remove.host}→${pair.add.host}`;
    (grouped.get(key) ?? grouped.set(key, []).get(key)!).push(pair);
  }
  for (const [key, list] of grouped) {
    list.sort((a, b) => a.remove.time - b.remove.time);
    const [from, to] = key.split('→');
    let group: typeof list = [];
    const flush = (): void => {
      if (group.length === 0) return;
      const devices = group.map((p) => ({
        vidPid: p.remove.event.vidPid!,
        name: p.remove.event.name ?? p.remove.event.vidPid!,
      }));
      migrations.push({
        from,
        to,
        at: group[0].remove.event.t,
        devices,
        branchRoot: devices.find((d) => hubVidPids.has(d.vidPid)),
      });
      group = [];
    };
    for (const pair of list) {
      if (group.length > 0 && pair.remove.time - group[group.length - 1].remove.time > GROUP_SPAN_MS) {
        flush();
      }
      group.push(pair);
    }
    flush();
  }
  migrations.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));

  return {
    migrations,
    unmatchedRemovals: removes.filter((r) => !r.matched).map(({ host, event }) => ({ host, event })),
    unmatchedAdds: adds.filter((a) => !a.matched).map(({ host, event }) => ({ host, event })),
  };
}
