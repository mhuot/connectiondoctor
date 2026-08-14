import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseEnvelope, parseEventStream } from '../contract/parse';
import { buildViewTree, type ViewNode } from './topology';
import { layoutDiagram, type DiagramStyle } from './layout';
import { detectMigrations } from './migrate';

const fixture = (name: string): string =>
  readFileSync(join(__dirname, '..', 'contract', 'fixtures', name), 'utf8');

const envelope = () => parseEnvelope(JSON.parse(fixture('surface-chain.v1.json')));

describe('buildViewTree', () => {
  it('physical mode folds silicon and same-enclosure nodes, never thunderbolt', () => {
    const full = buildViewTree(envelope(), 'full');
    const physical = buildViewTree(envelope(), 'physical');

    const count = (n: ViewNode): number => 1 + n.children.reduce((s, c) => s + count(c), 0);
    expect(count(physical)).toBeLessThan(count(full));

    const findKind = (n: ViewNode, kind: string): ViewNode | undefined =>
      n.kind === kind ? n : n.children.map((c) => findKind(c, kind)).find(Boolean);
    expect(findKind(physical, 'thunderbolt')).toBeDefined();

    // Folded devices are accounted, not silently hidden.
    const badges = (n: ViewNode): string[] => [...n.badges, ...n.children.flatMap(badges)];
    expect(badges(physical).some((b) => /\+\d+ internal/.test(b))).toBe(true);
  });

  it('merges the LG display into its own hub — one monitor, one box', () => {
    const full = buildViewTree(envelope(), 'full');
    let lg: ViewNode | undefined;
    const walk = (n: ViewNode): void => {
      if (n.title === 'LG ULTRAWIDE') lg = n;
      n.children.forEach(walk);
    };
    walk(full);
    // The anonymous "4-Port USB 2.0 Hub" was first vendor-resolved to LG, then
    // the display merged into it: it now carries both panel and hub roles.
    expect(lg).toBeDefined();
    expect(lg!.carriesDisplay).toBe(true);
    expect(lg!.children.length).toBeGreaterThan(0); // keyboard/mouse still behind it
    expect(lg!.badges.join(' ')).toMatch(/×/); // resolution badge present
  });

  it('resolves an anonymous hub via a same-vendor child when no display merges', () => {
    const tree = buildViewTree(
      {
        schema: 'connection-contract/v1',
        capturedAt: '2026-08-14T00:00:00Z',
        host: { name: 'x', os: 'macos', arch: 'arm64' },
        power: { source: 'mains', externalConnected: true, batteryPresent: false },
        nodes: [
          { id: 'h', kind: 'host', name: 'x', protocol: 'power' },
          { id: 'hub', parentId: 'h', kind: 'hub', name: '4-Port Hub', vendorName: 'Generic',
            vidPid: '043E:9C04', protocol: 'usb2' },
          { id: 'ctl', parentId: 'hub', kind: 'device', name: 'Monitor Controls',
            vendorName: 'LG Electronics Inc.', vidPid: '043E:9A39', protocol: 'usbLow' },
        ],
      },
      'full',
    );
    let titles: string[] = [];
    const walk = (n: ViewNode): void => { titles.push(n.title); n.children.forEach(walk); };
    walk(tree);
    expect(titles).toContain('4-Port Hub — LG Electronics Inc.');
  });

  it('dock-powered host gets the shared-cable power node', () => {
    const tree = buildViewTree(envelope(), 'physical');
    expect(tree.kind).toBe('power');
    expect(tree.title).toContain('Dock');
    expect(tree.note).toContain('same cable');
  });

  it('summarises tunnels on thunderbolt nodes and never marks usb2 tunneled', () => {
    const tree = buildViewTree(envelope(), 'full');
    let tbBadges: string[] = [];
    const walk = (n: ViewNode): void => {
      if (n.kind === 'thunderbolt') tbBadges = n.badges;
      if (n.protocol === 'usb2' || n.protocol === 'usbLow') expect(n.tunneled).toBe(false);
      n.children.forEach(walk);
    };
    walk(tree);
    expect(tbBadges.join(' ')).toMatch(/USB2 native/);
  });
});

describe('layoutDiagram', () => {
  const styles: DiagramStyle[] = ['cascade', 'topDown', 'flow'];

  it.each(styles)('%s: boxes never overlap and all edges are orthogonal', (style) => {
    const layout = layoutDiagram(buildViewTree(envelope(), 'physical'), style);

    for (let i = 0; i < layout.nodes.length; i++) {
      for (let j = i + 1; j < layout.nodes.length; j++) {
        const a = layout.nodes[i].frame;
        const b = layout.nodes[j].frame;
        const overlap =
          a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
        expect(overlap, `${layout.nodes[i].id} overlaps ${layout.nodes[j].id}`).toBe(false);
      }
    }
    for (const edge of layout.edges) {
      for (let k = 1; k < edge.points.length; k++) {
        const dx = edge.points[k].x - edge.points[k - 1].x;
        const dy = edge.points[k].y - edge.points[k - 1].y;
        expect(dx === 0 || dy === 0, `edge ${edge.id} segment ${k} is diagonal`).toBe(true);
      }
    }
  });

  it.each(styles)('%s: everything lands inside the reported canvas', (style) => {
    const layout = layoutDiagram(buildViewTree(envelope(), 'physical'), style);
    for (const p of layout.nodes) {
      expect(p.frame.x).toBeGreaterThanOrEqual(0);
      expect(p.frame.y).toBeGreaterThanOrEqual(0);
      expect(p.frame.x + p.frame.w).toBeLessThanOrEqual(layout.width);
      expect(p.frame.y + p.frame.h).toBeLessThanOrEqual(layout.height);
    }
  });

  it('boxes widen for long titles instead of clamping to minimum', () => {
    const layout = layoutDiagram(buildViewTree(envelope(), 'full'), 'cascade');
    const keyboard = layout.nodes.find((n) => n.node.title.includes('Magic Keyboard'));
    expect(keyboard).toBeDefined();
    expect(keyboard!.frame.w).toBeGreaterThan(150);
  });
});

describe('detectMigrations (the KVM case, from real recordings)', () => {
  const streams = () => [
    { host: 'mini', events: parseEventStream(fixture('kvm-mini.events.jsonl')).events },
    { host: 'surface', events: parseEventStream(fixture('kvm-surface.events.jsonl')).events },
  ];

  it('collapses a branch move into one migration, not five', () => {
    const result = detectMigrations(streams(), { hubVidPids: new Set(['043E:9C04']) });
    expect(result.migrations).toHaveLength(1);
    const m = result.migrations[0];
    expect(m.from).toBe('mini');
    expect(m.to).toBe('surface');
    expect(m.devices).toHaveLength(5);
    expect(m.branchRoot?.vidPid).toBe('043E:9C04');
    expect(result.unmatchedRemovals).toHaveLength(0);
    expect(result.unmatchedAdds).toHaveLength(0);
  });

  it('requires remove to precede add', () => {
    const result = detectMigrations([
      { host: 'a', events: [{ t: '2026-08-13T22:20:00Z', kind: 'deviceRemoved', vidPid: '046D:C08A' }] },
      { host: 'b', events: [{ t: '2026-08-13T22:19:00Z', kind: 'deviceAdded', vidPid: '046D:C08A' }] },
    ]);
    expect(result.migrations).toHaveLength(0);
    expect(result.unmatchedRemovals).toHaveLength(1);
    expect(result.unmatchedAdds).toHaveLength(1);
  });

  it('count-matches duplicate hardware instead of inventing migrations', () => {
    // Two identical mice exist; one remove on A, two adds on B → one migration,
    // one genuinely-new add.
    const result = detectMigrations([
      { host: 'a', events: [{ t: '2026-08-13T22:19:00Z', kind: 'deviceRemoved', vidPid: '046D:C08A', name: 'MX' }] },
      {
        host: 'b',
        events: [
          { t: '2026-08-13T22:19:30Z', kind: 'deviceAdded', vidPid: '046D:C08A', name: 'MX' },
          { t: '2026-08-13T22:19:31Z', kind: 'deviceAdded', vidPid: '046D:C08A', name: 'MX' },
        ],
      },
    ]);
    expect(result.migrations).toHaveLength(1);
    expect(result.migrations[0].devices).toHaveLength(1);
    expect(result.unmatchedAdds).toHaveLength(1);
  });

  it('outside the window is not a migration', () => {
    const result = detectMigrations(
      [
        { host: 'a', events: [{ t: '2026-08-13T22:19:00Z', kind: 'deviceRemoved', vidPid: '046D:C08A' }] },
        { host: 'b', events: [{ t: '2026-08-13T22:22:00Z', kind: 'deviceAdded', vidPid: '046D:C08A' }] },
      ],
      { windowMs: 120_000 },
    );
    expect(result.migrations).toHaveLength(0);
  });
});
