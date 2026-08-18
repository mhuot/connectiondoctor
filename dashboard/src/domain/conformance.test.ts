import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseEnvelope, parseEventStream } from '../contract/parse';
import { stitchIncidents } from './incidents';
import { loadFiles } from '../data/sources';
import { hostKey } from '../data/store';
import type { ContractEnvelope } from '../contract/types';

/**
 * The conformance corpus (docs/fixtures) run against the TypeScript engine.
 *
 * Be precise about what this file proves, because the corpus is only worth
 * what its weakest claim is worth.
 *
 * **Executed** — incident stitching. `stitchIncidents` is the one diagnosis
 * step that lives in TypeScript, so for incidents these tests are a real
 * engine producing a real answer that is compared against a fixture written
 * before the code. That is where a false alarm can actually be caught here,
 * and it has been: the five-second and two-minute deficit cases below pin
 * both edges of the sustained-deficit rule.
 *
 * **Asserted, not executed** — finding quality. Findings are produced by the
 * Swift and C# engines; nothing in this repo recomputes them from fixture
 * input in TypeScript. The finding assertions therefore check that each case
 * *declares* a coherent answer (a control declares silence, a fault declares
 * something loud) and that any findings the fixture envelope carries agree
 * with that declaration. A fixture whose expected findings were simply wrong
 * would pass. Closing that gap means running the Swift and C# engines over
 * this corpus — contract-conformance 1.3/1.4 — and until then task 1.1a is
 * partial by design, not by oversight.
 *
 * **Not attempted** — parity. Whether all three engines agree needs the other
 * two engines reading contract data; same follow-up, same reason.
 */
const FIXTURES = join(__dirname, '..', '..', '..', 'docs', 'fixtures');

interface Expected {
  kind: 'fault' | 'control' | 'identity';
  /** Identity cases only: what the loader must make of the documents. */
  hosts?: number;
  /** Identity cases only: reasons the unattributed entry must carry. */
  unattributed?: { events: number; reasonMatches: string };
  findings: Array<{ severity: string; title: string }>;
  incidents: Array<{ rootEvent: string | null; devicesLost: number; sharedParent: string | null }>;
  notes: string;
}

const cases = readdirSync(FIXTURES, { withFileTypes: true })
  .filter((e) => e.isDirectory())
  .map((e) => e.name)
  .sort();

/** Cases about *one* machine, which is every case that describes a diagnosis.
 *  The identity cases are about telling machines apart and carry two hosts, so
 *  they are held out of the single-envelope assertions rather than bent to
 *  fit them. */
const diagnosisCases = cases.filter((c) => !c.startsWith('identity-'));

/** Every contract document in a case, in filename order. Most cases have one
 *  (`contract.v1.json`); an identity case has `a.` and `b.` because the thing
 *  under test only exists between two documents. */
const contractsIn = (name: string): string[] =>
  readdirSync(join(FIXTURES, name))
    .filter((f) => f.endsWith('contract.v1.json'))
    .sort();

const load = (name: string) => {
  const dir = join(FIXTURES, name);
  const files = contractsIn(name);
  const raws = files.map((f) => JSON.parse(readFileSync(join(dir, f), 'utf8')) as Record<string, any>);
  const eventsFile = join(dir, 'events.v1.jsonl');
  const events = parseEventStream(existsSync(eventsFile) ? readFileSync(eventsFile, 'utf8') : '');
  const expected = JSON.parse(readFileSync(join(dir, 'expected.json'), 'utf8')) as Expected;
  return {
    raw: raws[0],
    raws,
    files,
    envelope: parseEnvelope(raws[0]) as ContractEnvelope,
    events,
    expected,
  };
};

describe('conformance corpus', () => {
  it('has both fault and control cases — controls are the half that stops false alarms', () => {
    expect(cases.filter((c) => c.startsWith('fault-')).length).toBeGreaterThan(0);
    expect(cases.filter((c) => c.startsWith('control-')).length).toBeGreaterThanOrEqual(5);
    expect(cases.filter((c) => c.startsWith('identity-')).length).toBeGreaterThanOrEqual(3);
  });

  it.each(cases)('%s: fixture is well formed and self-describing', (name) => {
    const { raws, envelope, events, expected } = load(name);
    expect(envelope.schema).toBe('connection-contract/v1');
    // A corpus that is not itself contract-valid proves nothing about an
    // engine that consumes the contract, so the identity rules from
    // docs/schema-v1.md are enforced on the fixtures themselves: host.id is a
    // random per-installation UUIDv4 (never hardware-derived) and unitKey is
    // the 16-hex truncation of an HMAC. A fixture carrying a serial number or
    // a hand-written string here would quietly bless the thing identity
    // forbids. Checked against the raw document rather than the parsed
    // envelope: both fields are still `opt, proposed` (issue #27) and the TS
    // parser does not model them yet, so parsing first would silently drop
    // exactly the values under test.
    // Every document in the case, not just the first: an identity case's whole
    // point lives in the second one, and checking only the first would leave
    // the document under test unvalidated.
    for (const doc of raws) {
      expect(doc.schema).toBe('connection-contract/v1');
      expect(doc.host.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
      for (const node of (doc.nodes ?? []) as Array<Record<string, unknown>>) {
        if (node.unitKey !== undefined) expect(node.unitKey).toMatch(/^[0-9a-f]{16}$/);
      }
    }
    expect(events.skippedLines).toBe(0);                 // a corrupt fixture would silently weaken every assertion
    expect(['fault', 'control', 'identity']).toContain(expected.kind);
    expect(expected.notes.length).toBeGreaterThan(40);   // why this answer is right, not just what it is
    expect(existsSync(join(FIXTURES, name, 'README.md'))).toBe(true);
    // Every case must state its provenance: a constructed case proves the
    // engine follows its rule; only a recording proves the rule matches reality.
    const readme = readFileSync(join(FIXTURES, name, 'README.md'), 'utf8');
    expect(readme).toMatch(/\((constructed|recorded)[^)]*\)/);
  });

  it.each(diagnosisCases)('%s: incident stitching (executed) matches expected shape', (name) => {
    const { envelope, events, expected } = load(name);
    const incidents = stitchIncidents(events.events, envelope);

    expect(incidents.length).toBe(expected.incidents.length);
    incidents.forEach((incident, i) => {
      const want = expected.incidents[i];
      expect(incident.devicesLost.length).toBe(want.devicesLost);
      expect(incident.rootEvent ?? null).toBe(want.rootEvent);
      expect(incident.sharedParent?.id ?? null).toBe(want.sharedParent);
    });
  });

  it.each(cases.filter((c) => c.startsWith('control-')))(
    '%s: a control declares silence, and its envelope findings agree',
    (name) => {
      const { envelope, expected } = load(name);
      expect(expected.findings).toEqual([]);
      // And the producer's own findings, when the fixture carries them, agree.
      const loud = (envelope.findings ?? []).filter((f) => f.severity !== 'info');
      expect(loud).toEqual([]);
    },
  );

  it.each(cases.filter((c) => c.startsWith('fault-')))(
    '%s: a fault declares a finding with a severity that demands attention',
    (name) => {
      const { expected } = load(name);
      expect(expected.findings.length).toBeGreaterThan(0);
      expect(expected.findings.every((f) => f.severity === 'warning' || f.severity === 'critical')).toBe(true);
      expect(expected.findings.every((f) => f.title.length > 0)).toBe(true);
    },
  );

  // The two deficit cases are the corpus's sharpest pair: same event kinds,
  // same shape, different duration, opposite answers. They are what stops the
  // shallow-dip fix from being implemented as "ignore power entirely".
  it('a momentary dip is silent and a sustained deficit is not — duration is the difference', () => {
    const deficitSeconds = (name: string) => {
      const { events } = load(name);
      const start = events.events.find((e) => e.kind === 'deficitStart');
      const end = events.events.find((e) => e.kind === 'deficitEnd');
      expect(start && end).toBeTruthy();
      return (Date.parse(end!.t) - Date.parse(start!.t)) / 1000;
    };

    const shallow = load('control-shallow-deficit');
    expect(deficitSeconds('control-shallow-deficit')).toBeLessThan(10);
    expect(stitchIncidents(shallow.events.events, shallow.envelope)).toEqual([]);

    const sustained = load('fault-power-deficit');
    expect(deficitSeconds('fault-power-deficit')).toBeGreaterThanOrEqual(120);
    // A sustained deficit reaches the timeline even though nothing was lost,
    // and even though this fixture's producer emitted no incidents of its own.
    expect(stitchIncidents(sustained.events.events, sustained.envelope).length).toBe(1);
  });

  // The case that is *not* in the corpus as a file, because it is about what
  // is absent: a deficit that started and never ended. A machine still short
  // of power says so once and then goes quiet, so the stream is a start
  // followed by nothing but snapshots — and measuring the episode to the last
  // ordinary event makes it zero seconds long. The longer the fault runs
  // unresolved, the more certain the silence would be.
  it('a deficit that never ended is measured against the latest evidence, not the last event', () => {
    const { envelope } = load('fault-power-deficit');
    const at = (minutes: number) => new Date(Date.UTC(2026, 6, 4, 9, minutes)).toISOString();
    const snapshotEvent = { t: at(20), kind: 'fullSnapshot' as const, snapshot: envelope };

    // Start, then only a snapshot: still open, and it has been open 20 minutes.
    expect(stitchIncidents([{ t: at(0), kind: 'deficitStart' }, snapshotEvent], envelope).length).toBe(1);

    // The envelope's own capture time is evidence too, so a lone start with a
    // later envelope raises just the same — that is the live case, where the
    // deficit is happening right now and the stream holds one event.
    expect(stitchIncidents([{ t: at(0), kind: 'deficitStart' }], { ...envelope, capturedAt: at(20) }).length).toBe(1);

    // The ceiling is the data's own last moment and never the clock: an
    // envelope captured at the same instant the deficit began vouches for
    // nothing after it, so reading this file next year gives today's answer.
    expect(stitchIncidents([{ t: at(0), kind: 'deficitStart' }], { ...envelope, capturedAt: at(0) })).toEqual([]);
    expect(stitchIncidents([{ t: at(0), kind: 'deficitStart' }])).toEqual([]);
  });

  // A later timestamp is not proof of continuity, and this is the distinction
  // that keeps "unresolved for twenty minutes" from being asserted over a hole
  // in the recording where the deficitEnd may well be sitting.
  it('a deficit keeps its recorded transitions and claims a duration only over a complete window', () => {
    const { envelope } = load('fault-power-deficit');
    const at = (minutes: number) => new Date(Date.UTC(2026, 6, 4, 9, minutes)).toISOString();
    const start = { t: at(0), kind: 'deficitStart' as const };
    const coverage = (complete: boolean, reasons?: string[]) => ({
      ...envelope,
      capturedAt: at(20),
      analysis: {
        windowHours: 6,
        generatedAt: at(20),
        coverage: { availableFrom: at(0), through: at(20), complete, ...(reasons ? { reasons } : {}) },
      },
    });

    // Continuous recording, still open: the supply has been short the whole
    // twenty minutes and saying so is a fact, not an inference.
    const proven = stitchIncidents([start], coverage(true));
    expect(proven).toHaveLength(1);
    expect(proven[0].deficit).toEqual({ since: at(0), durationProven: true });

    // A gap in the same window: the end may be inside it. The start is still
    // worth showing — a deficit began and we lost the recording — but the
    // duration is not ours to claim.
    const unproven = stitchIncidents([start], coverage(false, ['gap']));
    expect(unproven).toHaveLength(1);
    expect(unproven[0].deficit).toEqual({ since: at(0), durationProven: false });

    // No coverage at all is unknown, not health: absent ≠ complete.
    const uncovered = { ...envelope, capturedAt: at(20), analysis: undefined };
    expect(stitchIncidents([start], uncovered)[0].deficit?.durationProven).toBe(false);

    // A *closed* pair over an incomplete window keeps both transitions and
    // still claims no duration. The two events prove the machine was short of
    // power at T0 and had recovered by T5; they do not prove five continuous
    // minutes, because an end we missed followed by a restart looks identical
    // from here — and `coverage.complete` is a boolean over the whole window
    // with no interval that could place the gap outside the pair.
    const closedInGap = stitchIncidents([start, { t: at(5), kind: 'deficitEnd' }], coverage(false, ['gap']));
    expect(closedInGap).toHaveLength(1);
    expect(closedInGap[0].deficit).toEqual({ since: at(0), until: at(5), durationProven: false });

    // The same pair over a window the producer vouches for: now it is five
    // continuous minutes, and that is the only case where we say so.
    const closedProven = stitchIncidents([start, { t: at(5), kind: 'deficitEnd' }], coverage(true));
    expect(closedProven[0].deficit).toEqual({ since: at(0), until: at(5), durationProven: true });

    // And a short pair stays silent however incomplete the window is: a hole
    // between two transitions can only make the real deficit shorter, never
    // longer, so five seconds is certainly not sustained.
    const brief = [start, { t: new Date(Date.UTC(2026, 6, 4, 9, 0, 5)).toISOString(), kind: 'deficitEnd' as const }];
    expect(stitchIncidents(brief, coverage(false, ['gap']))).toEqual([]);
  });

  // `complete: true` is a claim about [availableFrom, through] and nothing
  // outside it. Treating it as a global flag blesses spans the recorder never
  // saw, which is exactly what imported history looks like.
  it('completeness vouches for an interval, not for every event that was imported', () => {
    const { envelope } = load('fault-power-deficit');
    const at = (hour: number, minute = 0) => new Date(Date.UTC(2026, 6, 4, hour, minute)).toISOString();
    const covered = (from: string, to: string, extra: Record<string, unknown> = {}) => ({
      ...envelope,
      capturedAt: to,
      analysis: {
        windowHours: 6,
        generatedAt: to,
        coverage: { availableFrom: from, through: to, complete: true, ...extra },
      },
    });

    // Recorded before the window opened: the producer never saw it, however
    // complete the window it did see.
    const before = stitchIncidents(
      [{ t: at(8), kind: 'deficitStart' }, { t: at(8, 5), kind: 'deficitEnd' }],
      covered(at(10), at(16)),
    );
    expect(before[0].deficit).toEqual({ since: at(8), until: at(8, 5), durationProven: false });

    // An open episode that began before the window: the recorder cannot vouch
    // for the part that predates it, so the duration is not claimed at all.
    const crossing = stitchIncidents([{ t: at(8), kind: 'deficitStart' }], covered(at(10), at(16)));
    expect(crossing[0].deficit).toEqual({ since: at(8), durationProven: false });

    // Evidence past `through` — a stray imported event an hour later — says
    // nothing about whether the supply was still short. The episode is capped
    // at the boundary rather than discarded, so the part the recorder did see
    // is still proven.
    const past = stitchIncidents(
      [{ t: at(12), kind: 'deficitStart' }, { t: at(17), kind: 'linkUp' }],
      covered(at(10), at(16)),
    );
    expect(past[0].deficit).toEqual({ since: at(12), durationProven: true });

    // And when the cap leaves too little inside the window to be sustained,
    // nothing is proven — the later evidence cannot make up the difference.
    // Thirty seconds before `through`, with an event an hour past it: the
    // hour is real and is why this reaches the timeline at all, but only the
    // thirty seconds are vouched for, and thirty seconds is not sustained.
    const lateStart = new Date(Date.UTC(2026, 6, 4, 15, 59, 30)).toISOString();
    const barely = stitchIncidents(
      [{ t: lateStart, kind: 'deficitStart' }, { t: at(17), kind: 'linkUp' }],
      covered(at(10), at(16)),
    );
    expect(barely[0].deficit).toEqual({ since: lateStart, durationProven: false });

    // Bounds that are absent or unparseable are unknown, not health.
    const unparseable = stitchIncidents(
      [{ t: at(12), kind: 'deficitStart' }],
      covered('not-a-date', at(16)),
    );
    expect(unparseable[0].deficit?.durationProven).toBe(false);
  });

  it('an incomplete window is never reported as health', () => {
    const { envelope, expected } = load('control-incomplete-history');
    expect(envelope.analysis?.coverage.complete).toBe(false);
    expect(expected.findings).toEqual([]);
    // The emptiness means "unknown", and the envelope carries the reason why.
    expect(envelope.analysis?.coverage.reasons?.length).toBeGreaterThan(0);
  });

  // Identity cases run through the real loader, so unlike finding quality this
  // is *executed*: `loadFiles` + `hostKey` are the engine, the fixtures are the
  // input, and a regression in either shows up here rather than in a comment.
  describe('identity (executed: loadFiles + hostKey)', () => {
    const identityCases = cases.filter((c) => c.startsWith('identity-'));

    const filesFor = (name: string): File[] => {
      const dir = join(FIXTURES, name);
      return readdirSync(dir)
        .filter((f) => f.endsWith('.json') && f !== 'expected.json')
        .concat(readdirSync(dir).filter((f) => f.endsWith('.jsonl')))
        .sort()
        .map((f) => new File([readFileSync(join(dir, f), 'utf8')], f));
    };

    it.each(identityCases)('%s: the loader reaches the expected host count', async (name) => {
      const { expected } = load(name);
      const files = filesFor(name);
      const hosts = await loadFiles(files, []);
      expect(hosts).toHaveLength(expected.hosts!);
    });

    // Dropping several files at once says nothing about which the browser
    // hands over first, so any answer that depends on the order is a bug that
    // reproduces on someone else's machine and not on ours.
    it.each(identityCases)('%s: the answer does not depend on file order', async (name) => {
      const files = filesFor(name);
      const forwards = await loadFiles(files, []);
      const backwards = await loadFiles([...files].reverse(), []);

      const keys = (hosts: Awaited<ReturnType<typeof loadFiles>>) =>
        hosts.map((h) => `${hostKey(h)}:${h.events.length}`).sort();
      expect(keys(backwards)).toEqual(keys(forwards));
    });

    it('a renamed machine is one endpoint that keeps its history', async () => {
      const hosts = await loadFiles(filesFor('identity-rename'), []);
      const { raws } = load('identity-rename');

      expect(hosts).toHaveLength(1);
      expect(hostKey(hosts[0])).toBe(raws[0].host.id);
      // The later document wins the display name; the id is what joined them.
      expect(hosts[0].name).toBe('mac-mini-office');
      expect(hosts[0].events).toHaveLength(1);
      expect(raws[0].host.id).toBe(raws[1].host.id);
      expect(raws[0].host.name).not.toBe(raws[1].host.name);
    });

    it('two machines sharing a hostname stay two, and their stream joins neither', async () => {
      const { expected } = load('identity-hostname-reuse');
      const hosts = await loadFiles(filesFor('identity-hostname-reuse'), []);

      const identified = hosts.filter((h) => h.envelope);
      expect(identified).toHaveLength(2);
      expect(new Set(identified.map(hostKey)).size).toBe(2);
      // Neither machine absorbed history that might not be its own.
      expect(identified.every((h) => h.events.length === 0)).toBe(true);

      const orphan = hosts.find((h) => !h.envelope)!;
      expect(orphan.events).toHaveLength(expected.unattributed!.events);
      expect(orphan.historyReasons.join(' ')).toContain(expected.unattributed!.reasonMatches);
    });

    it('the same dock on two endpoints keys differently, and that is the limit not the answer', async () => {
      const { raws } = load('identity-duplicate-docks');
      const hosts = await loadFiles(filesFor('identity-duplicate-docks'), []);
      expect(hosts).toHaveLength(2);

      const keyOf = (doc: Record<string, any>) =>
        (doc.nodes as Array<Record<string, any>>).find((n) => n.unitKey)!.unitKey as string;
      const [a, b] = raws.map(keyOf);

      // Same model, same reported serial, different installations: the keys
      // must differ, because a shared secret would make two people's docks
      // correlate across exports.
      expect(a).not.toBe(b);
      expect(raws[0].nodes[1].vidPid).toBe(raws[1].nodes[1].vidPid);
      // And nothing in this repo joins them back up — asserted by absence,
      // which is why the README says so rather than the test proving it.
      expect(new Set([a, b]).size).toBe(2);
    });
  });
});
