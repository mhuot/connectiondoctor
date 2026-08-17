import { parseEnvelope, parseEventStream } from '../contract/parse';
import type { ContractEvent } from '../contract/types';
import { emptyContact, hostKey, mergeRefresh, type HostData } from './store';

/** Loads dropped/picked files: `.jsonl` → event stream, otherwise envelope.
 *  Host identity comes from the envelope, or the filename for bare streams. */
export async function loadFiles(files: File[], existing: HostData[]): Promise<HostData[]> {
  // Keyed by identity: two envelopes with the same hostname and different
  // host.id are two machines and must not merge, and a renamed machine must
  // not split. Event-only files have no envelope, so they key on their derived
  // name until an envelope for the same host arrives.
  const hosts = new Map(existing.map((h) => [hostKey(h), { ...h }]));
  for (const file of files) {
    const text = await file.text();
    if (file.name.endsWith('.jsonl')) {
      const { events, skippedLines } = parseEventStream(text);
      // Where a stream says who it belongs to, in the order the answers can be
      // trusted. A sync point inside the stream carries the producer's own host
      // record and is the only self-describing answer; a filename is a
      // convention, and the name the recorder actually writes — `events.jsonl`,
      // or `events.v1.jsonl` in a bundle — says nothing about the machine at
      // all. Reading "events" out of that as a hostname was worse than
      // admitting ignorance: it matched no envelope, so the ordinary at-home
      // workflow of dropping the contract and the stream together split one
      // machine into topology here and history there.
      const fromStream = hostFromSnapshot(events);
      const fromFilename = hostNameFromFile(file.name);
      const name = fromStream?.name ?? fromFilename;
      const candidates = [...hosts.values()].filter((h) =>
        fromStream?.id !== undefined
          // The stream names its own machine: its id is the answer, and an
          // events-only host of that name is the same machine waiting for one.
          ? (h.envelope ? h.envelope.host.id === fromStream.id : h.name === fromStream.name)
          : name !== undefined
            ? h.name === name
            // Nothing names a host. One loaded host is not a guess; more is.
            : true,
      );

      // While the attribution is ambiguous the events accumulate in one place,
      // rather than each stream creating a fresh entry at the same key and
      // silently taking the previous one's events with it.
      const orphan = hosts.get(`name:${name ?? UNATTRIBUTED}`);
      const host = candidates.length === 1
        ? candidates[0]
        : (candidates.length > 1 && orphan?.envelope === undefined ? orphan : undefined)
          ?? { name: name ?? UNATTRIBUTED, events: [], origin: file.name, contact: emptyContact(), historyReasons: [] };

      // More than one candidate is a question this file cannot answer, and
      // guessing would attach one machine's history to another — the one
      // mistake that makes a timeline lie. It stays unattributed and says why.
      // Counted excluding the entry being written into: on a second ambiguous
      // stream the orphan is itself a candidate, and counting it would phrase
      // the same problem differently each time and stack up near-duplicates.
      const rivals = candidates.filter((c) => c !== host).length;
      if (candidates.length > 1) {
        host.historyReasons = [...new Set([...host.historyReasons, ambiguity(file.name, name, rivals)])];
      }
      host.events = [...host.events, ...events];
      host.origin = `${host.origin === file.name ? '' : `${host.origin}, `}${file.name}`;
      host.contact = { ...host.contact, eventsAt: new Date().toISOString(), skippedLines: host.contact.skippedLines + skippedLines };
      if (skippedLines > 0) host.historyReasons = [...new Set([...host.historyReasons, `${skippedLines} skipped lines`])];
      hosts.set(hostKey(host), host);
    } else {
      const envelope = parseEnvelope(JSON.parse(text));
      const name = envelope.host.name;
      // An envelope carrying an id adopts events already loaded under that
      // name — the same machine, now identified. It may only do so while the
      // name is unambiguous: once a second identified host shares it, adopting
      // would be the same guess the stream branch above refuses to make.
      // Events dropped alongside this envelope may have landed here first and
      // named nobody — `events.jsonl` says nothing, so the stream branch parks
      // them under UNATTRIBUTED. If that entry is the only thing loaded there
      // is no one else they could belong to, which is the same "one host is
      // not a guess" rule the stream branch applies in the other direction.
      const anonymous = hosts.get(`name:${UNATTRIBUTED}`);
      const soleAnonymous = anonymous !== undefined && hosts.size === 1 ? anonymous : undefined;
      const unattributed = hosts.get(`name:${name}`) ?? soleAnonymous;
      const contested = [...hosts.values()].some(
        (h) => h.name === name && h.envelope !== undefined && h.envelope.host.id !== envelope.host.id,
      );
      const host = hosts.get(envelope.host.id ?? `name:${name}`)
        ?? (contested ? undefined : unattributed)
        ?? { name, events: [], origin: file.name, contact: emptyContact(), historyReasons: [] };
      if (envelope.host.id && host === unattributed) hosts.delete(`name:${host.name}`);
      if (envelope.host.id && contested && unattributed && unattributed !== host) {
        unattributed.historyReasons = [
          ...new Set([
            ...unattributed.historyReasons,
            `more than one host is named "${name}" — these events are not attributed to any of them`,
          ]),
        ];
      }
      host.name = name;
      host.envelope = envelope;
      host.contact = { ...host.contact, contractAt: new Date().toISOString() };
      hosts.set(hostKey(host), host);
    }
  }
  return [...hosts.values()];
}

/** What we call a stream whose machine we cannot name. Stable, so two such
 *  streams accumulate in one entry instead of overwriting each other. */
const UNATTRIBUTED = 'unattributed';

/** Names the recorder and the bundle write for *any* host. Reading a hostname
 *  out of one of these is how `events.jsonl` became a machine called "events"
 *  that matched nothing. */
const GENERIC_STREAM_NAMES = new Set(['events', 'events.v1', 'contract', 'contract.v1']);

function hostNameFromFile(filename: string): string | undefined {
  // "kvm-mini.events.jsonl" and "mini.events.jsonl" → "mini";
  // "events.jsonl" and "events.v1.jsonl" → nobody.
  const base = filename.replace(/\.(events\.)?jsonl$/, '');
  const dash = base.lastIndexOf('-');
  const name = dash >= 0 ? base.slice(dash + 1) : base;
  return name.length > 0 && !GENERIC_STREAM_NAMES.has(name) ? name : undefined;
}

/** The host a stream describes itself as belonging to: the `host` record on
 *  its most recent sync point. This is the producer's own answer rather than a
 *  filename convention, so it wins wherever both exist. */
function hostFromSnapshot(events: ContractEvent[]): { id?: string; name: string } | undefined {
  for (let i = events.length - 1; i >= 0; i--) {
    const host = events[i].snapshot?.host;
    if (host) return { id: host.id, name: host.name };
  }
  return undefined;
}

function ambiguity(filename: string, name: string | undefined, count: number): string {
  return name === undefined
    ? `${filename}: names no host and ${count} are loaded — events not attributed to any of them`
    : `${filename}: ${count} hosts named "${name}" — events not attributed to any of them`;
}

/** Fetch a collector endpoint (TBDoctor --serve): /contract plus /events.
 *  Same Source boundary as files — views never know the difference. Errors
 *  name the URL and the cause so a fleet refresh can fail per-host. */
export async function loadHttp(baseUrl: string, previous?: HostData): Promise<HostData> {
  let envelope;
  try {
    const response = await fetch(new URL('/contract', baseUrl));
    if (!response.ok) throw new Error(`GET /contract → HTTP ${response.status}`);
    envelope = parseEnvelope(await response.json());
  } catch (cause) {
    throw new Error(`${baseUrl}: ${cause instanceof Error ? cause.message : String(cause)}`);
  }
  const now = new Date().toISOString();

  // Events are fetched separately and their failure is *state*, not silence:
  // an unreachable /events must not sink the envelope, but it must not become
  // an empty stream either — that would let the timeline say "no incidents"
  // about evidence it never saw. mergeRefresh keeps the previous events,
  // marked, when this fetch fails.
  let events: HostData['events'] = [];
  let skippedLines = 0;
  let eventsError: string | undefined;
  try {
    const response = await fetch(new URL('/events', baseUrl));
    if (!response.ok) throw new Error(`GET /events → HTTP ${response.status}`);
    ({ events, skippedLines } = parseEventStream(await response.text()));
  } catch (cause) {
    eventsError = cause instanceof Error ? cause.message : String(cause);
  }

  const fresh: HostData = {
    name: envelope.host.name, envelope, events, origin: baseUrl,
    contact: { contractAt: now, eventsAt: eventsError ? undefined : now, eventsError, skippedLines },
    historyReasons: [],
  };
  return mergeRefresh(previous, fresh);
}

/** Re-fetch every HTTP-origin host. Atomic per host: a failure keeps that
 *  host's previous data and reports the error; others still update. */
export async function refreshHttpHosts(
  hosts: HostData[],
): Promise<{ hosts: HostData[]; errors: string[] }> {
  const errors: string[] = [];
  const next = await Promise.all(
    hosts.map(async (host) => {
      if (!host.origin.startsWith('http')) return host;
      try {
        return await loadHttp(host.origin, host);
      } catch (error) {
        // /contract failed: keep the whole previous host — envelope *and*
        // events together, so stale incidents are never paired with fresh
        // events — and record the failure on the host itself.
        const message = error instanceof Error ? error.message : String(error);
        errors.push(message);
        return { ...host, contact: { ...host.contact, contractError: message, eventsError: host.contact.eventsError ?? 'not attempted (contract failed)' } };
      }
    }),
  );
  return { hosts: next, errors };
}
