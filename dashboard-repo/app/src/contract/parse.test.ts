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
