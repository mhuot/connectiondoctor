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

/** Future: poll a collector's HTTP endpoint. Same interface as files, so the
 *  views never know the difference. Not wired to UI yet (see tasks 4.2). */
export async function loadHttp(baseUrl: string): Promise<HostData> {
  const response = await fetch(new URL('/contract', baseUrl));
  if (!response.ok) throw new Error(`GET /contract → ${response.status}`);
  const envelope = parseEnvelope(await response.json());
  return { name: envelope.host.name, envelope, events: [], origin: baseUrl };
}
