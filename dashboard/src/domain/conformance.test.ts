import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseEnvelope, parseEventStream } from '../contract/parse';
import { stitchIncidents } from './incidents';
import type { ContractEnvelope } from '../contract/types';

/**
 * The conformance corpus (docs/fixtures) run against the TypeScript engine.
 *
 * Two questions are kept apart on purpose. **Parity** — do Swift, C# and TS
 * agree — cannot be answered here; it needs the other two engines reading
 * contract data (contract-conformance 1.3/1.4). **Diagnostic quality** — is
 * the answer right — can be, and is what this file asserts. The rule that
 * matters most: no control case may produce a warning or a critical finding.
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
  const envelope = parseEnvelope(JSON.parse(readFileSync(join(dir, 'contract.v1.json'), 'utf8'))) as ContractEnvelope;
  const events = parseEventStream(readFileSync(join(dir, 'events.v1.jsonl'), 'utf8'));
  const expected = JSON.parse(readFileSync(join(dir, 'expected.json'), 'utf8')) as Expected;
  return { envelope, events, expected };
};

describe('conformance corpus', () => {
  it('has both fault and control cases — controls are the half that stops false alarms', () => {
    expect(cases.filter((c) => c.startsWith('fault-')).length).toBeGreaterThan(0);
    expect(cases.filter((c) => c.startsWith('control-')).length).toBeGreaterThanOrEqual(5);
  });

  it.each(cases)('%s: fixture is well formed and self-describing', (name) => {
    const { envelope, events, expected } = load(name);
    expect(envelope.schema).toBe('connection-contract/v1');
    expect(events.skippedLines).toBe(0);                 // a corrupt fixture would silently weaken every assertion
    expect(['fault', 'control']).toContain(expected.kind);
    expect(expected.notes.length).toBeGreaterThan(40);   // why this answer is right, not just what it is
    expect(existsSync(join(FIXTURES, name, 'README.md'))).toBe(true);
    // Every case must state its provenance: a constructed case proves the
    // engine follows its rule; only a recording proves the rule matches reality.
    const readme = readFileSync(join(FIXTURES, name, 'README.md'), 'utf8');
    expect(readme).toMatch(/\((constructed|recorded)[^)]*\)/);
  });

  it.each(cases)('%s: incident stitching matches expected shape', (name) => {
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
    '%s: a control never raises a warning or critical finding',
    (name) => {
      const { envelope, expected } = load(name);
      expect(expected.findings).toEqual([]);
      // And the producer's own findings, when the fixture carries them, agree.
      const loud = (envelope.findings ?? []).filter((f) => f.severity !== 'info');
      expect(loud).toEqual([]);
    },
  );

  it.each(cases.filter((c) => c.startsWith('fault-')))(
    '%s: a fault names a finding with a severity that demands attention',
    (name) => {
      const { expected } = load(name);
      expect(expected.findings.length).toBeGreaterThan(0);
      expect(expected.findings.every((f) => f.severity === 'warning' || f.severity === 'critical')).toBe(true);
      expect(expected.findings.every((f) => f.title.length > 0)).toBe(true);
    },
  );

  it('an incomplete window is never reported as health', () => {
    const { envelope, expected } = load('control-incomplete-history');
    expect(envelope.analysis?.coverage.complete).toBe(false);
    expect(expected.findings).toEqual([]);
    // The emptiness means "unknown", and the envelope carries the reason why.
    expect(envelope.analysis?.coverage.reasons?.length).toBeGreaterThan(0);
  });
});
