import { describe, expect, it } from 'vitest';
import { emptyContact, hostContact, hostHistory, hostKey, mergeRefresh, type HostData } from './store';
import { ifMatchHeader } from './baseline';
import { deviceCountSeries } from '../domain/series';
import { parseEnvelope, parseEventStream, ContractError } from '../contract/parse';
import type { ContractEnvelope, ContractEvent } from '../contract/types';

const envelope = (extra: Record<string, unknown> = {}): ContractEnvelope => parseEnvelope({
  schema: 'connection-contract/v1', capturedAt: '2026-08-17T00:00:00Z',
  host: { name: 'mini', os: 'macos', arch: 'arm64' },
  power: { source: 'mains', externalConnected: true, batteryPresent: false },
  nodes: [
    { id: 'host', kind: 'host', name: 'mini', protocol: 'power' },
    { id: 'usb:1', parentId: 'host', kind: 'hub', name: 'Hub', protocol: 'usb3' },
    { id: 'usb:2', parentId: 'usb:1', kind: 'device', name: 'Mouse', protocol: 'usb2' },
  ],
  ...extra,
});
const complete = { windowHours: 6, generatedAt: '2026-08-17T00:00:00Z',
  coverage: { availableFrom: '2026-08-16T18:00:00Z', through: '2026-08-17T00:00:00Z', complete: true } };
const host = (over: Partial<HostData> = {}): HostData => ({
  name: 'mini', origin: 'http://mini:8787', events: [], contact: emptyContact(), historyReasons: [], ...over,
});
const now = Date.parse('2026-08-17T00:00:30Z');

describe('strict parse (issue #47)', () => {
  it('rejects string "false" for coverage.complete', () => {
    expect(() => envelope({ analysis: { ...complete, coverage: { ...complete.coverage, complete: 'false' } } })).toThrow(ContractError);
  });
  it('rejects non-array findings/incidents containers and unknown baseline states', () => {
    expect(() => envelope({ findings: {} })).toThrow(/findings must be an array/);
    expect(() => envelope({ incidents: 'none' })).toThrow(/incidents must be an array/);
    expect(() => envelope({ analysis: { ...complete, baseline: { state: 'fine' } } })).toThrow(/baseline.state/);
  });
  it('requires a recommendation on every finding', () => {
    expect(() => envelope({ analysis: complete, findings: [{ severity: 'info', title: 't', explanation: 'e', evidence: ['x'] }] })).toThrow(/recommendation/);
  });
  it('rejects unknown incident rootEvent kinds and non-numeric windowHours', () => {
    expect(() => envelope({ analysis: complete, incidents: [{ start: '2026-08-17T00:00:00Z', rootEvent: 'meteor' }] })).toThrow(/rootEvent/);
    expect(() => envelope({ analysis: { ...complete, windowHours: '6' } })).toThrow(/windowHours/);
  });
});

describe('host contact and history (issue #47)', () => {
  it('absent findings stay unknown even with complete coverage', () => {
    const h = host({ envelope: envelope({ analysis: complete }), contact: { ...emptyContact(), contractAt: '2026-08-17T00:00:00Z', eventsAt: '2026-08-17T00:00:00Z' } });
    expect(h.envelope?.findings).toBeUndefined();
    expect(hostHistory(h).state).toBe('complete'); // the window is complete…
    // …but the *findings* field is absent: FindingsView must not say "No findings" (asserted in the view contract, here we pin the data).
  });

  it('a failed /events keeps the previous events, marks envelope-only/incomplete, and never reads healthy', () => {
    const prev = host({ envelope: envelope({ analysis: complete }), events: [{ t: '2026-08-16T23:00:00Z', kind: 'linkDown' }],
      contact: { ...emptyContact(), contractAt: '2026-08-16T23:59:00Z', eventsAt: '2026-08-16T23:59:00Z' } });
    const fresh = host({ envelope: envelope({ analysis: complete }), events: [],
      contact: { ...emptyContact(), contractAt: '2026-08-17T00:00:00Z', eventsError: 'GET /events → HTTP 500' } });
    const merged = mergeRefresh(prev, fresh);
    expect(merged.events).toHaveLength(1);              // stale events retained, not blanked
    expect(merged.contact.eventsError).toMatch(/500/);
    expect(hostHistory(merged).state).toBe('incomplete');
    expect(hostHistory(merged).reasons.join()).toMatch(/events-fetch-failed/);
    expect(hostContact(merged, now)).toBe('live');       // contact is a separate axis
  });

  it('a failed /events with no previous events is envelope-only', () => {
    const fresh = host({ envelope: envelope({ analysis: complete }), contact: { ...emptyContact(), contractAt: '2026-08-17T00:00:00Z', eventsError: 'timeout' } });
    expect(hostHistory(mergeRefresh(undefined, fresh)).state).toBe('envelope-only');
  });

  it('skipped lines are durable: a later clean fetch that does not prove completeness keeps the reason', () => {
    const corrupt = host({ envelope: envelope({ analysis: complete }), events: [], contact: { ...emptyContact(), contractAt: 'x', eventsAt: 'x', skippedLines: 3 } });
    const first = mergeRefresh(undefined, corrupt);
    expect(hostHistory(first).state).toBe('incomplete');
    // Next payload: producer says incomplete (recorder restarted) → reasons persist
    const later = host({ envelope: envelope({ analysis: { ...complete, coverage: { ...complete.coverage, complete: false, reasons: ['gap'] } } }), events: [], contact: { ...emptyContact(), contractAt: 'y', eventsAt: 'y' } });
    const second = mergeRefresh(first, later);
    expect(second.historyReasons).toContain('3 skipped lines');
    expect(hostHistory(second).reasons).toEqual(expect.arrayContaining(['3 skipped lines', 'gap']));
    // A payload that proves the window complete with zero skipped lines clears them
    const clean = host({ envelope: envelope({ analysis: complete }), events: [], contact: { ...emptyContact(), contractAt: 'z', eventsAt: 'z' } });
    expect(mergeRefresh(second, clean).historyReasons).toEqual([]);
    expect(hostHistory(mergeRefresh(second, clean)).state).toBe('complete');
  });

  it('a later envelope without analysis does not clear an existing host reason', () => {
    const first = mergeRefresh(undefined, host({ envelope: envelope({ analysis: complete }), contact: { ...emptyContact(), contractAt: 'x', eventsAt: 'x', skippedLines: 1 } }));
    const noAnalysis = host({ envelope: envelope(), contact: { ...emptyContact(), contractAt: 'y', eventsAt: 'y' } });
    expect(mergeRefresh(first, noAnalysis).historyReasons).toContain('1 skipped lines');
  });

  it('contact: live within the window, stale after, offline when both halves failed', () => {
    const live = host({ contact: { ...emptyContact(), contractAt: '2026-08-17T00:00:10Z' } });
    expect(hostContact(live, now)).toBe('live');
    const stale = host({ contact: { ...emptyContact(), contractAt: '2026-08-16T23:00:00Z' } });
    expect(hostContact(stale, now)).toBe('stale');
    const off = host({ contact: { ...emptyContact(), contractAt: '2026-08-17T00:00:10Z', contractError: 'down', eventsError: 'down' } });
    expect(hostContact(off, now)).toBe('offline');
  });

  it('a stale envelope is never paired with fresh events: a failed /contract keeps the whole previous host', () => {
    // refreshHttpHosts keeps `host` untouched apart from contact errors when /contract fails; pin the merge helper's atomicity from the other side:
    const prev = host({ envelope: envelope({ analysis: complete, incidents: [] }), events: [{ t: '2026-08-16T23:00:00Z', kind: 'linkDown' }], contact: { ...emptyContact(), contractAt: 'a', eventsAt: 'a' } });
    const merged = mergeRefresh(prev, host({ envelope: envelope({ analysis: complete, incidents: [] }), events: [], contact: { ...emptyContact(), contractAt: 'b', eventsError: 'boom' } }));
    expect(merged.envelope?.incidents).toEqual([]);      // fresh envelope…
    expect(merged.events).toHaveLength(1);              // …with the previous events retained and flagged, so the pairing is visible
    expect(merged.contact.eventsAt).toBe('a');
  });
});

describe('device-count series anchoring (issue #47)', () => {
  const env2 = envelope();
  const snap = (t: string, n: number): ContractEvent => ({ t, kind: 'fullSnapshot', snapshot: envelope({ nodes: [
    { id: 'host', kind: 'host', name: 'mini', protocol: 'power' },
    ...Array.from({ length: n }, (_, i) => ({ id: `usb:${i}`, parentId: 'host', kind: 'device', name: `d${i}`, protocol: 'usb2' })),
  ] }) });

  it('anchors on the last fullSnapshot and applies only later deltas', () => {
    const events: ContractEvent[] = [
      { t: '2026-08-16T20:00:00Z', kind: 'deviceRemoved', nodeId: 'usb:9' }, // before the anchor: ignored for counting
      snap('2026-08-16T21:00:00Z', 5),
      { t: '2026-08-16T21:30:00Z', kind: 'deviceRemoved', nodeId: 'usb:1' },
      { t: '2026-08-16T21:31:00Z', kind: 'deviceAdded', nodeId: 'usb:1' },
      { t: '2026-08-16T21:32:00Z', kind: 'deviceAdded', nodeId: 'usb:7' },
    ];
    const s = deviceCountSeries(events, env2);
    expect(s.anchoredAt).toBe('2026-08-16T21:00:00Z');
    expect(s.points[0]).toEqual({ t: '2026-08-16T21:00:00Z', count: 5 });
    expect(s.points.at(-1)?.count).toBe(6);
  });

  it('without a snapshot, infers backwards so the series ends at the current envelope', () => {
    const events: ContractEvent[] = [
      { t: '2026-08-16T21:30:00Z', kind: 'deviceRemoved' },
      { t: '2026-08-16T21:31:00Z', kind: 'deviceRemoved' },
    ];
    const s = deviceCountSeries(events, env2); // env2 has 2 countable nodes now
    expect(s.anchoredAt).toBeUndefined();
    expect(s.points[0].count).toBe(4);
    expect(s.points.at(-1)?.count).toBe(2);
  });
});

describe('post-merge honesty follow-ups (review of #53)', () => {
  it('a stream whose every line is corrupt is incomplete, not "never recorded"', () => {
    const h = host({ events: [], contact: { ...emptyContact(), contractAt: 'x', eventsAt: 'x', skippedLines: 12 }, historyReasons: ['12 skipped lines'] });
    const state = hostHistory(h);
    expect(state.state).toBe('incomplete');
    expect(state.reasons.join()).toMatch(/12 skipped lines/);
  });

  it('still reports no-history when there is genuinely nothing to explain', () => {
    expect(hostHistory(host({ events: [], contact: { ...emptyContact(), contractAt: 'x', eventsAt: 'x' } })).state).toBe('no-history');
  });

  it('a series that contradicts the current envelope is unknown, not clamped', () => {
    // Four net additions, but the current envelope has only 2 countable nodes:
    // the start would have to be negative, so history is missing (Copilot's
    // "current=1 with three net additions ends at 3" case).
    const events: ContractEvent[] = [
      { t: '2026-08-16T21:00:00Z', kind: 'deviceAdded' },
      { t: '2026-08-16T21:01:00Z', kind: 'deviceAdded' },
      { t: '2026-08-16T21:02:00Z', kind: 'deviceAdded' },
      { t: '2026-08-16T21:03:00Z', kind: 'deviceAdded' },
    ];
    const s = deviceCountSeries(events, envelope()); // envelope() has 2 countable nodes
    expect(s.points).toEqual([]);
    expect(s.contradiction).toMatch(/more device/);
  });

  it('a fullSnapshot without an envelope is a skipped line, not a silent anchor loss', () => {
    const stream = [
      JSON.stringify({ t: '2026-08-16T21:00:00Z', kind: 'fullSnapshot' }),           // no snapshot
      JSON.stringify({ t: '2026-08-16T21:01:00Z', kind: 'deviceRemoved' }),
    ].join('\n');
    const { events, skippedLines, lastSnapshotIndex } = parseEventStream(stream);
    expect(skippedLines).toBe(1);
    expect(events).toHaveLength(1);
    expect(lastSnapshotIndex).toBeNull();
  });
});

describe('baseline mutation client shape (review of #55)', () => {
  it('sends If-Match as one quoted ETag, which is what the server accepts', () => {
    expect(ifMatchHeader('2026-08-16T09:00:00.0000000-05:00')).toBe('"2026-08-16T09:00:00.0000000-05:00"');
  });
});

describe('baseline availability is separate from history coverage (review of #57)', () => {
  it('parses capabilities.baseline and keeps coverage untouched', () => {
    const env = parseEnvelope({
      schema: 'connection-contract/v1', capturedAt: '2026-08-17T00:00:00Z',
      host: { name: 'surface', os: 'windows', arch: 'arm64' },
      power: { source: 'dock', externalConnected: true, batteryPresent: true },
      nodes: [{ id: 'host', kind: 'host', name: 'surface', protocol: 'power' }],
      analysis: {
        windowHours: 6, generatedAt: '2026-08-17T00:00:00Z',
        coverage: { availableFrom: '2026-08-16T18:00:00Z', through: '2026-08-17T00:00:00Z', complete: true },
        capabilities: { linkEvents: 'unavailable', baseline: 'busy' },
      },
      findings: [],
    });
    expect(env.analysis?.coverage.complete).toBe(true);       // history is complete…
    expect(env.analysis?.capabilities?.baseline).toBe('busy'); // …and the baseline is unknown
    expect(env.analysis?.baseline).toBeUndefined();
  });

  it('rejects an unknown or wrong-typed capability instead of formatting it later', () => {
    const withCapabilities = (capabilities: unknown) => () => parseEnvelope({
      schema: 'connection-contract/v1', capturedAt: '2026-08-17T00:00:00Z',
      host: { name: 'surface', os: 'windows', arch: 'arm64' },
      power: { source: 'dock', externalConnected: true, batteryPresent: true },
      nodes: [{ id: 'host', kind: 'host', name: 'surface', protocol: 'power' }],
      analysis: {
        windowHours: 6, generatedAt: '2026-08-17T00:00:00Z',
        coverage: { availableFrom: '2026-08-16T18:00:00Z', through: '2026-08-17T00:00:00Z', complete: true },
        capabilities,
      },
    });

    expect(withCapabilities({ baseline: 7 })).toThrow(/capabilities.baseline/);
    expect(withCapabilities({ baseline: 'sort-of' })).toThrow(/capabilities.baseline/);
    expect(withCapabilities({ linkEvents: 'psychic' })).toThrow(/capabilities.linkEvents/);
    expect(withCapabilities('yes')).toThrow(/capabilities/);
    expect(withCapabilities({})).not.toThrow();
  });
});

describe('coverage reason vocabulary is extensible (review of #58)', () => {
  it('an unrecognised reason keeps the host incomplete and is shown, not rejected', () => {
    const env = parseEnvelope({
      schema: 'connection-contract/v1', capturedAt: '2026-08-17T00:00:00Z',
      host: { name: 'surface', os: 'windows', arch: 'arm64' },
      power: { source: 'dock', externalConnected: true, batteryPresent: true },
      nodes: [{ id: 'host', kind: 'host', name: 'surface', protocol: 'power' }],
      analysis: {
        windowHours: 6, generatedAt: '2026-08-17T00:00:00Z',
        coverage: {
          availableFrom: '2026-08-16T18:00:00Z', through: '2026-08-17T00:00:00Z', complete: false,
          reasons: ['solar-flare', 'corrupt-lines'],
        },
      },
      findings: [],
    });
    expect(env.analysis?.coverage.reasons).toEqual(['solar-flare', 'corrupt-lines']);

    const h = host({ envelope: env, contact: { ...emptyContact(), contractAt: 'x', eventsAt: 'x' } });
    const state = hostHistory(h);
    expect(state.state).toBe('incomplete');
    expect(state.reasons).toEqual(expect.arrayContaining(['solar-flare', 'corrupt-lines']));
  });
});

describe('host identity (issue #27)', () => {
  const envelopeWith = (extra: Record<string, unknown>) => parseEnvelope({
    schema: 'connection-contract/v1', capturedAt: '2026-08-17T00:00:00Z',
    host: { name: 'mini', os: 'macos', arch: 'arm64', ...extra },
    power: { source: 'mains', externalConnected: true, batteryPresent: false },
    nodes: [{ id: 'host', kind: 'host', name: 'mini', protocol: 'power' }],
  });

  it('a renamed machine is still one endpoint', () => {
    const before = host({ name: 'mini', envelope: envelopeWith({ id: 'abc-123' }) });
    const after = host({ name: 'mac-mini-office', envelope: parseEnvelope({
      schema: 'connection-contract/v1', capturedAt: '2026-08-17T01:00:00Z',
      host: { name: 'mac-mini-office', os: 'macos', arch: 'arm64', id: 'abc-123' },
      power: { source: 'mains', externalConnected: true, batteryPresent: false },
      nodes: [{ id: 'host', kind: 'host', name: 'mac-mini-office', protocol: 'power' }],
    }) });
    expect(hostKey(after)).toBe(hostKey(before));
  });

  it('two machines that share a hostname stay two endpoints', () => {
    const a = host({ name: 'surface', envelope: envelopeWith({ id: 'aaa' }) });
    const b = host({ name: 'surface', envelope: envelopeWith({ id: 'bbb' }) });
    expect(hostKey(a)).not.toBe(hostKey(b));
  });

  it('falls back to the name for producers that predate host.id', () => {
    const legacy = host({ name: 'm3pro', envelope: envelopeWith({}) });
    expect(hostKey(legacy)).toBe('name:m3pro');
    expect(hostKey(host({ name: 'm3pro' }))).toBe('name:m3pro');   // events-only host
  });

  it('parses host.id and node unitKey without requiring either', () => {
    const env = envelopeWith({ id: 'abc-123' });
    expect(env.host.id).toBe('abc-123');
    expect(envelopeWith({}).host.id).toBeUndefined();
  });
});
