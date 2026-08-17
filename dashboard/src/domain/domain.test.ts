import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseEnvelope, parseEventStream } from '../contract/parse';
import { buildTopology, buildViewTree, builtInChip, filterBuiltIn, modeChip, type ViewNode } from './topology';
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

describe('topology controls (dashboard-topology-controls, issues #42 #43)', () => {
  /** A Surface-class laptop: integrated panel/touch/HID hang off an internal
   *  root hub, and so does the external dock branch — the shape that makes a
   *  naive "hide built-in" filter orphan what you plugged in. */
  const surfaceLike = () => parseEnvelope({
    schema: 'connection-contract/v1', capturedAt: '2026-08-17T00:00:00Z',
    host: { name: 'surface', os: 'windows', arch: 'arm64' },
    power: { source: 'dock', externalConnected: true, batteryPresent: true },
    displaysKnown: false,
    nodes: [
      { id: 'host', kind: 'host', name: 'surface', protocol: 'power' },
      { id: 'usb:root', parentId: 'host', kind: 'hub', name: 'USB Root Hub (USB 3.0)', protocol: 'usb3', builtIn: true },
      { id: 'usb:panel', parentId: 'usb:root', kind: 'device', name: 'Surface Calibrated Panel', protocol: 'usb2', builtIn: true },
      { id: 'usb:touch', parentId: 'usb:root', kind: 'device', name: 'Surface Touch Screen Device', protocol: 'usb2', builtIn: true },
      { id: 'usb:tp', parentId: 'usb:root', kind: 'hub', name: 'Surface Touchpad Device', protocol: 'usb2', builtIn: true, vidPid: '045E:0001', vendorName: 'Microsoft' },
      { id: 'usb:tp1', parentId: 'usb:tp', kind: 'device', name: 'Surface Touchpad Configuration', protocol: 'usb2', builtIn: true, vidPid: '045E:0002', vendorName: 'Microsoft' },
      { id: 'usb:tp2', parentId: 'usb:tp', kind: 'device', name: 'Surface HID Mouse', protocol: 'usb2', builtIn: true, vidPid: '045E:0003', vendorName: 'Microsoft' },
      { id: 'tb:dock', parentId: 'usb:root', kind: 'thunderbolt', name: 'Surface Thunderbolt 4 Dock', protocol: 'thunderbolt', builtIn: false },
      { id: 'usb:lghub', parentId: 'tb:dock', kind: 'hub', name: '4-Port USB 2.0 Hub', vendorName: 'LG Electronics Inc.', vidPid: '043E:9C04', protocol: 'usb2', builtIn: false },
      { id: 'usb:kbd', parentId: 'usb:lghub', kind: 'device', name: 'Magic Keyboard', vidPid: '05AC:029F', protocol: 'usb2', builtIn: false },
      { id: 'usb:unknown', parentId: 'usb:lghub', kind: 'device', name: 'USB Input Device', protocol: 'usb2' },
    ],
  });
  const ids = (n: ViewNode): string[] => [n.id, ...n.children.flatMap(ids)];

  it('default off hides integrated devices but keeps the internal root hub that feeds the dock', () => {
    const { root, stats } = buildTopology(surfaceLike(), 'full', { includeBuiltIn: false });
    const seen = ids(root);
    expect(seen).not.toContain('usb:panel');
    expect(seen).not.toContain('usb:touch');
    expect(seen).not.toContain('usb:tp1');
    expect(seen).toContain('usb:root');     // built-in, but external below it
    expect(seen).toContain('tb:dock');
    expect(seen).toContain('usb:lghub');
    expect(seen).toContain('usb:kbd');
    expect(seen).toContain('usb:unknown');  // absent builtIn = unknown, never hidden
    expect(stats.builtInHidden).toBe(5);
    expect(builtInChip(stats)).toBe('5 built-in hidden');
  });

  it('on shows everything and says so', () => {
    const { root, stats } = buildTopology(surfaceLike(), 'full', { includeBuiltIn: true });
    expect(ids(root)).toContain('usb:panel');
    expect(stats.builtInHidden).toBe(0);
    expect(builtInChip(stats)).toBe('0 built-in hidden');
  });

  it('filterBuiltIn is a pure contract-level operation', () => {
    const { nodes, hidden, total } = filterBuiltIn(surfaceLike().nodes);
    expect(hidden).toBe(5); expect(total).toBe(6);
    expect(nodes.map((n) => n.id)).toContain('usb:root');
  });

  it('mode chip changes without any scrolling: folded count vs surfaced count', () => {
    // With built-ins shown, the touchpad hub folds its same-vendor children in
    // physical mode — the "first surfaced node below the fold" case from #42.
    const physical = buildTopology(surfaceLike(), 'physical', { includeBuiltIn: true });
    const full = buildTopology(surfaceLike(), 'full', { includeBuiltIn: true });
    expect(physical.stats.folded).toBeGreaterThan(0);
    expect(modeChip('physical', physical.stats)).toMatch(/^\d+ internal folded into \d+ container/);
    expect(modeChip('full', full.stats)).toBe(`${physical.stats.folded} surfaced`);
    expect(full.stats).toEqual(physical.stats); // accounting is mode-independent
  });

  it('real recording: physical fold accounting matches the +N internal badges', () => {
    const { root, stats } = buildTopology(envelope(), 'physical', { includeBuiltIn: true });
    const badges = (n: ViewNode): number[] => [
      ...n.badges.filter((b) => b.endsWith(' internal')).map((b) => parseInt(b, 10)),
      ...n.children.flatMap(badges)];
    const fromBadges = badges(root);
    expect(fromBadges.reduce((a, b) => a + b, 0)).toBe(stats.folded);
    expect(fromBadges.length).toBe(stats.containers);
    expect(stats.builtInTotal).toBe(0); // this producer build predates builtIn
    expect(builtInChip(stats)).toBeUndefined();
  });
});
