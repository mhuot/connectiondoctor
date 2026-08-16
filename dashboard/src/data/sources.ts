import { parseEnvelope, parseEventStream } from '../contract/parse';
import type { HostData } from './store';

/** Loads dropped/picked files: `.jsonl` → event stream, otherwise envelope.
 *  Host identity comes from the envelope, or the filename for bare streams. */
export async function loadFiles(files: File[], existing: HostData[]): Promise<HostData[]> {
  const hosts = new Map(existing.map((h) => [h.name, { ...h }]));
  for (const file of files) {
    const text = await file.text();
    if (file.name.endsWith('.jsonl')) {
      const { events } = parseEventStream(text);
      const name = hostNameFromFile(file.name);
      const host = hosts.get(name) ?? { name, events: [], origin: file.name };
      host.events = [...host.events, ...events];
      host.origin = `${host.origin === file.name ? '' : `${host.origin}, `}${file.name}`;
      hosts.set(name, host);
    } else {
      const envelope = parseEnvelope(JSON.parse(text));
      const name = envelope.host.name;
      const host = hosts.get(name) ?? { name, events: [], origin: file.name };
      host.envelope = envelope;
      hosts.set(name, host);
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
export async function loadHttp(baseUrl: string): Promise<HostData> {
  let envelope;
  try {
    const response = await fetch(new URL('/contract', baseUrl));
    if (!response.ok) throw new Error(`GET /contract → HTTP ${response.status}`);
    envelope = parseEnvelope(await response.json());
  } catch (cause) {
    throw new Error(`${baseUrl}: ${cause instanceof Error ? cause.message : String(cause)}`);
  }

  // Events are best-effort: a collector that has recorded nothing yet serves
  // an empty stream, and an unreachable /events must not sink the envelope.
  let events: HostData['events'] = [];
  try {
    const response = await fetch(new URL('/events', baseUrl));
    if (response.ok) events = parseEventStream(await response.text()).events;
  } catch {
    /* envelope-only host */
  }

  return { name: envelope.host.name, envelope, events, origin: baseUrl };
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
        return await loadHttp(host.origin);
      } catch (error) {
        errors.push(error instanceof Error ? error.message : String(error));
        return host;
      }
    }),
  );
  return { hosts: next, errors };
}
