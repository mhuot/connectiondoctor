import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseEnvelope, parseEventStream } from '../contract/parse';
import { stitchIncidents } from './incidents';
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
  kind: 'fault' | 'control';
  findings: Array<{ severity: string; title: string }>;
  incidents: Array<{ rootEvent: string | null; devicesLost: number; sharedParent: string | null }>;
  notes: string;
}

const cases = readdirSync(FIXTURES, { withFileTypes: true })
  .filter((e) => e.isDirectory())
  .map((e) => e.name)
  .sort();

const load = (name: string) => {
  const dir = join(FIXTURES, name);
  const raw = JSON.parse(readFileSync(join(dir, 'contract.v1.json'), 'utf8')) as Record<string, any>;
  const envelope = parseEnvelope(raw) as ContractEnvelope;
  const events = parseEventStream(readFileSync(join(dir, 'events.v1.jsonl'), 'utf8'));
  const expected = JSON.parse(readFileSync(join(dir, 'expected.json'), 'utf8')) as Expected;
  return { raw, envelope, events, expected };
};

describe('conformance corpus', () => {
  it('has both fault and control cases — controls are the half that stops false alarms', () => {
    expect(cases.filter((c) => c.startsWith('fault-')).length).toBeGreaterThan(0);
    expect(cases.filter((c) => c.startsWith('control-')).length).toBeGreaterThanOrEqual(5);
  });

  it.each(cases)('%s: fixture is well formed and self-describing', (name) => {
    const { raw, envelope, events, expected } = load(name);
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
    expect(raw.host.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    for (const node of (raw.nodes ?? []) as Array<Record<string, unknown>>) {
      if (node.unitKey !== undefined) expect(node.unitKey).toMatch(/^[0-9a-f]{16}$/);
    }
    expect(events.skippedLines).toBe(0);                 // a corrupt fixture would silently weaken every assertion
    expect(['fault', 'control']).toContain(expected.kind);
    expect(expected.notes.length).toBeGreaterThan(40);   // why this answer is right, not just what it is
    expect(existsSync(join(FIXTURES, name, 'README.md'))).toBe(true);
    // Every case must state its provenance: a constructed case proves the
    // engine follows its rule; only a recording proves the rule matches reality.
    const readme = readFileSync(join(FIXTURES, name, 'README.md'), 'utf8');
    expect(readme).toMatch(/\((constructed|recorded)[^)]*\)/);
  });

  it.each(cases)('%s: incident stitching (executed) matches expected shape', (name) => {
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

  it('an incomplete window is never reported as health', () => {
    const { envelope, expected } = load('control-incomplete-history');
    expect(envelope.analysis?.coverage.complete).toBe(false);
    expect(expected.findings).toEqual([]);
    // The emptiness means "unknown", and the envelope carries the reason why.
    expect(envelope.analysis?.coverage.reasons?.length).toBeGreaterThan(0);
  });
});
