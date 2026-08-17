import { parseEnvelope, parseEventStream } from '../contract/parse';
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
      const name = hostNameFromFile(file.name);
      // A bare stream knows only a name, and the host it belongs to may already
      // be here under its id — the ordinary order is envelope first, then its
      // events. Looking only under `name:` would miss it and split one machine
      // into two entries, one with topology and one with history.
      const named = [...hosts.values()].filter((h) => h.name === name);
      const host = named.length === 1
        ? named[0]
        : { name, events: [], origin: file.name, contact: emptyContact(), historyReasons: [] };
      // More than one host of that name is a question this file cannot answer,
      // and guessing would attach one machine's history to another — the one
      // mistake that makes a timeline lie. It stays unattributed and says why.
      if (named.length > 1) {
        host.historyReasons = [
          ...host.historyReasons,
          `${file.name}: ${named.length} hosts named "${name}" — events not attributed to any of them`,
        ];
      }
      host.events = [...host.events, ...events];
      host.origin = `${host.origin === file.name ? '' : `${host.origin}, `}${file.name}`;
      host.contact = { ...host.contact, eventsAt: new Date().toISOString(), skippedLines: host.contact.skippedLines + skippedLines };
      if (skippedLines > 0) host.historyReasons = [...new Set([...host.historyReasons, `${skippedLines} skipped lines`])];
      hosts.set(hostKey(host), host);
    } else {
      const envelope = parseEnvelope(JSON.parse(text));
      const name = envelope.host.name;
      // An envelope carrying an id adopts any events already loaded under that
      // name — the same machine, now identified — and thereafter keys on the id.
      // An envelope carrying an id adopts events already loaded under that
      // name — the same machine, now identified. It may only do so while the
      // name is unambiguous: once a second identified host shares it, adopting
      // would be the same guess the stream branch above refuses to make.
      const unattributed = hosts.get(`name:${name}`);
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

function hostNameFromFile(filename: string): string {
  // "kvm-mini.events.jsonl" → "mini"; fall back to the basename.
  const base = filename.replace(/\.(events\.)?jsonl$/, '');
  const dash = base.lastIndexOf('-');
  return dash >= 0 ? base.slice(dash + 1) : base;
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
