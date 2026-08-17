import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  ContractError,
  buildTree,
  parseEnvelope,
  parseEventStream,
  parseFinding,
} from './parse';
import { isDeficit } from './types';

const fixture = (name: string): string =>
  readFileSync(join(__dirname, 'fixtures', name), 'utf8');

describe('parseEnvelope', () => {
  it('parses a real recording (M3 Pro + Surface TB4 dock chain)', () => {
    const env = parseEnvelope(JSON.parse(fixture('surface-chain.v1.json')));
    expect(env.host.name).toBe('m3pro');
    expect(env.power.source).toBe('dock');
    expect(env.nodes.length).toBe(18);
    // The LG hub is identifiable by VID:PID regardless of its useless name.
    const lgHub = env.nodes.find((n) => n.vidPid === '043E:9C04');
    expect(lgHub).toBeDefined();
  });

  it('rejects wrong schema without partial data', () => {
    expect(() => parseEnvelope({ schema: 'connection-contract/v2', nodes: [] })).toThrow(
      ContractError,
    );
    expect(() => parseEnvelope({ nodes: [] })).toThrow(/connection-contract\/v1/);
  });

  it('never lets USB 2.0 claim to be tunneled', () => {
    const env = parseEnvelope(JSON.parse(fixture('surface-chain.v1.json')));
    for (const node of env.nodes) {
      if (node.protocol === 'usb2' || node.protocol === 'usbLow') {
        expect(node.tunneled).toBe(false);
      }
    }
  });

  it('tolerates unknown fields (additive-only rule)', () => {
    const doc = JSON.parse(fixture('surface-chain.v1.json'));
    doc.futureField = { anything: true };
    doc.nodes[0].alsoFuture = 42;
    expect(() => parseEnvelope(doc)).not.toThrow();
  });

  it('rejects duplicate node ids', () => {
    const doc = JSON.parse(fixture('surface-chain.v1.json'));
    doc.nodes.push({ ...doc.nodes[1] });
    expect(() => parseEnvelope(doc)).toThrow(/duplicate/);
  });
});

describe('buildTree', () => {
  it('reconstructs the dock chain from parentId alone', () => {
    const env = parseEnvelope(JSON.parse(fixture('surface-chain.v1.json')));
    const { roots, orphanIds } = buildTree(env.nodes);
    expect(orphanIds).toEqual([]);
    expect(roots.map((r) => r.node.kind)).toEqual(['host']);
    const host = roots[0];
    expect(host.children[0].node.kind).toBe('thunderbolt');
  });

  it('attaches orphans at root and flags them, never drops them', () => {
    const { roots, orphanIds } = buildTree([
      { id: 'a', kind: 'device', name: 'A', protocol: 'usb2', parentId: 'missing' },
    ]);
    expect(orphanIds).toEqual(['a']);
    expect(roots).toHaveLength(1);
  });
});

describe('parseEventStream', () => {
  it('loads the real KVM-flip removals (mini, 22:19:06Z)', () => {
    const result = parseEventStream(fixture('kvm-mini.events.jsonl'));
    expect(result.events).toHaveLength(5);
    expect(result.skippedLines).toBe(0);
    expect(result.events.every((e) => e.kind === 'deviceRemoved')).toBe(true);
  });

  it('skips corrupt lines while counting them', () => {
    const result = parseEventStream(fixture('kvm-surface.events.jsonl'));
    expect(result.events).toHaveLength(5); // the "not json at all" line
    expect(result.skippedLines).toBe(1);
  });
});

describe('parseFinding', () => {
  it('rejects findings without evidence', () => {
    expect(() =>
      parseFinding({ severity: 'critical', title: 't', explanation: 'e', evidence: [] }),
    ).toThrow(/evidence/);
  });
});

describe('isDeficit', () => {
  it('matches the shared 2000mW threshold', () => {
    const base = { source: 'dock' as const, externalConnected: true, batteryPresent: true };
    expect(isDeficit({ ...base, batteryRateMilliwatts: -10500 })).toBe(true);
    expect(isDeficit({ ...base, batteryRateMilliwatts: -1999 })).toBe(false);
    expect(isDeficit({ ...base, externalConnected: false, batteryRateMilliwatts: -10500 })).toBe(false);
  });
});

describe('live producer round-trip', () => {
  it('parses a contract emitted by TBDoctor on an M4 mini (desktop)', () => {
    const env = parseEnvelope(JSON.parse(fixture('mini-desktop.v1.json')));
    expect(env.power.source).toBe('mains');
    expect(env.power.batteryPresent).toBe(false);
    expect(env.host.model).toBe('Mac16,10');
    expect(buildTree(env.nodes).orphanIds).toEqual([]);
    // No Thunderbolt link on this machine — nothing may claim tunneling.
    expect(env.nodes.every((n) => !n.tunneled)).toBe(true);
  });
});

describe('parseEnvelope — findings, incidents, analysis (contract-findings-incidents)', () => {
  const base = () => JSON.parse(fixture('surface-chain.v1.json')) as Record<string, unknown>;
  const analysis = {
    windowHours: 6,
    generatedAt: '2026-08-16T12:04:00Z',
    coverage: { availableFrom: '2026-08-16T06:04:00Z', through: '2026-08-16T12:03:55Z', complete: true },
    baseline: { state: 'no-baseline' },
    capabilities: { linkEvents: 'kernel' },
  };

  it('absent block stays absent — "nothing recorded" is not "nothing found"', () => {
    const env = parseEnvelope(base());
    expect(env.findings).toBeUndefined();
    expect(env.incidents).toBeUndefined();
    expect(env.analysis).toBeUndefined();
  });

  it('empty arrays with complete coverage are the healthy negative case', () => {
    const env = parseEnvelope({ ...base(), findings: [], incidents: [], analysis });
    expect(env.findings).toEqual([]);
    expect(env.incidents).toEqual([]);
    expect(env.analysis?.coverage.complete).toBe(true);
    expect(env.analysis?.baseline?.state).toBe('no-baseline');
  });

  it('parses ranked findings and incidents with vidPid, sharedParent and power', () => {
    const env = parseEnvelope({
      ...base(),
      analysis,
      findings: [{
        severity: 'critical', title: 'Power supply under-served', confidence: 'high',
        explanation: 'Battery discharged while on AC.',
        evidence: ['Battery supplied up to 10.5W while the machine reported AC power'],
        recommendation: 'Use a higher-rated adapter.',
      }],
      incidents: [{
        start: '2026-08-16T10:00:00Z', end: '2026-08-16T10:00:20Z', rootEvent: 'linkDown',
        devicesLost: [{ vidPid: '046d:c08a', name: 'MX Vertical' }, { name: 'Anonymous hub' }],
        sharedParent: 'usb:0x00120000', power: { peakDischargeMilliwatts: -879 },
      }],
    });
    expect(env.findings?.[0].severity).toBe('critical');
    expect(env.findings?.[0].evidence).toHaveLength(1);
    expect(env.incidents?.[0].devicesLost?.[0].vidPid).toBe('046D:C08A');
    expect(env.incidents?.[0].sharedParent).toBe('usb:0x00120000');
    expect(env.incidents?.[0].power?.peakDischargeMilliwatts).toBe(-879);
  });

  it('rejects a present-but-invalid finding instead of dropping it silently', () => {
    expect(() => parseEnvelope({
      ...base(), analysis,
      findings: [{ severity: 'critical', title: 'x', explanation: 'y', evidence: [] }],
    })).toThrow(ContractError);
    expect(() => parseEnvelope({
      ...base(), analysis,
      findings: [{ severity: 2, title: 'x', explanation: 'y', evidence: ['z'] }],
    })).toThrow(/severity/);
  });

  it('keeps temporal coverage reasons so the UI can say "unknown", not "none"', () => {
    const env = parseEnvelope({
      ...base(), findings: [], incidents: [],
      analysis: { ...analysis, coverage: { ...analysis.coverage, complete: false, reasons: ['recorder-started-inside-window', 'gap'] } },
    });
    expect(env.analysis?.coverage.complete).toBe(false);
    expect(env.analysis?.coverage.reasons).toEqual(['recorder-started-inside-window', 'gap']);
  });
});
