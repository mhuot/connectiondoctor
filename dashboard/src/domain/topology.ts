/**
 * View-model construction: contract nodes → renderable tree, with the physical
 * collapse ported from TBDoctor's Topology engine. The rules each encode a
 * mistake made during the original investigation — see design.md.
 */

import type {
  ContractDisplay,
  ContractEnvelope,
  ContractNode,
  LinkProtocol,
  NodeKind,
} from '../contract/types';
import { buildTree, type TreeNode } from '../contract/parse';

export type TopoMode = 'physical' | 'full';

export interface Detail {
  label: string;
  value: string;
  searchable?: boolean;
}

export interface ViewNode {
  id: string;
  kind: NodeKind;
  title: string;
  badges: string[];
  note?: string;
  protocol: LinkProtocol;
  tunneled: boolean;
  vidPid?: string;
  details: Detail[];
  internalCount: number;
  carriesDisplay: boolean;
  children: ViewNode[];
}

/** VIDs of hub-controller silicon — always inside some other product, never a
 *  box you could point at. Folded in physical mode. */
const CONTROLLER_SILICON = new Set(['8087', '05E3', '1D5C', '2109', '1A40']);

/** A Thunderbolt switch reports its controller silicon's vendor (Intel on both
 *  docks in this fleet), so TB nodes are never vendor-folded. */
const vendorKey = (node: ContractNode): string | undefined =>
  node.vidPid?.slice(0, 4) ?? node.vendorName;

export interface TopologyOptions {
  /** Show nodes the producer marked `builtIn`. Default false: a dock-fault
   *  tool is about what you plugged in; integrated devices are hidden — and
   *  counted, so nothing disappears silently. */
  includeBuiltIn: boolean;
}

export interface TopologyStats {
  /** Physical-mode accounting, computed regardless of the mode shown. */
  folded: number;
  containers: number;
  /** Built-in nodes hidden by the filter (0 when the filter is off). */
  builtInHidden: number;
  /** Built-in nodes the producer marked, whether shown or hidden. */
  builtInTotal: number;
}

export interface Topology {
  root: ViewNode;
  stats: TopologyStats;
}

/** The view tree plus the numbers the controls need to give feedback that does
 *  not depend on scroll position (issue #42) and to say how many built-ins are
 *  hidden (issue #43). */
export function buildTopology(env: ContractEnvelope, mode: TopoMode, options: TopologyOptions): Topology {
  const { nodes, hidden, total } = options.includeBuiltIn
    ? { nodes: env.nodes, hidden: 0, total: env.nodes.filter((n) => n.builtIn).length }
    : filterBuiltIn(env.nodes);
  const filtered: ContractEnvelope = { ...env, nodes };
  const physical = buildViewTree(filtered, 'physical');
  const root = mode === 'physical' ? physical : buildViewTree(filtered, 'full');
  const { folded, containers } = foldStats(physical);
  return { root, stats: { folded, containers, builtInHidden: hidden, builtInTotal: total } };
}

/** Text for the mode chip: what a switch changes, readable without scrolling. */
export function modeChip(mode: TopoMode, stats: TopologyStats): string {
  if (stats.folded === 0) return 'nothing folded';
  return mode === 'physical'
    ? `${stats.folded} internal folded into ${stats.containers} container${stats.containers === 1 ? '' : 's'}`
    : `${stats.folded} surfaced`;
}

/** Text for the built-in chip; only shown when the producer classified any.
 *  Always the hidden count — one quantity, comparable before and after the
 *  toggle ("6 built-in hidden" → "0 built-in hidden"). */
export function builtInChip(stats: TopologyStats): string | undefined {
  if (stats.builtInTotal === 0) return undefined;
  return `${stats.builtInHidden} built-in hidden`;
}

/** Drop nodes the producer marked built-in — but only when nothing external
 *  hangs below them: an internal root hub that feeds an external port stays,
 *  because removing it would orphan what you plugged in. Absent `builtIn`
 *  is unknown and is never hidden. */
export function filterBuiltIn(nodes: ContractNode[]): { nodes: ContractNode[]; hidden: number; total: number } {
  const hasExternalBelow = new Map<string, boolean>();
  const childrenOf = new Map<string, ContractNode[]>();
  for (const n of nodes) {
    if (n.parentId) childrenOf.set(n.parentId, [...(childrenOf.get(n.parentId) ?? []), n]);
  }
  const external = (n: ContractNode): boolean => {
    const cached = hasExternalBelow.get(n.id);
    if (cached !== undefined) return cached;
    hasExternalBelow.set(n.id, false); // cycle guard
    const own = n.builtIn !== true;
    const below = (childrenOf.get(n.id) ?? []).some(external);
    const result = own || below;
    hasExternalBelow.set(n.id, result);
    return result;
  };
  const kept = nodes.filter((n) => n.builtIn !== true || external(n));
  const total = nodes.filter((n) => n.builtIn === true).length;
  return { nodes: kept, hidden: nodes.length - kept.length, total };
}

function foldStats(root: ViewNode): { folded: number; containers: number } {
  let folded = 0, containers = 0;
  const walk = (n: ViewNode): void => {
    if (n.internalCount > 0) { folded += n.internalCount; containers += 1; }
    n.children.forEach(walk);
  };
  walk(root);
  return { folded, containers };
}

export function buildViewTree(env: ContractEnvelope, mode: TopoMode): ViewNode {
  const { roots } = buildTree(env.nodes);
  const power = powerNode(env);
  const children = roots.map(toView);
  // Producers emit host as a root; if absent, synthesize one so the power
  // node always has something to feed.
  let host =
    children.find((c) => c.kind === 'host') ??
    ({
      id: 'host',
      kind: 'host',
      title: env.host.name,
      badges: [],
      protocol: 'power',
      tunneled: false,
      details: [],
      internalCount: 0,
      carriesDisplay: false,
      children,
    } satisfies ViewNode);
  host.badges = hostBadges(env);
  host.title = host.title === 'host' ? env.host.name : host.title;

  attachDisplays(env, host);
  if (mode === 'physical') host = collapse(host, env.nodes);
  summariseTunnels(host);

  power.children = [host];
  return power;
}

function toView(tree: TreeNode): ViewNode {
  const n = tree.node;
  const view: ViewNode = {
    id: n.id,
    kind: n.kind,
    title: displayTitle(n, tree.children.map((c) => c.node)),
    badges: badges(n, tree.children.length),
    protocol: n.protocol,
    tunneled: Boolean(n.tunneled),
    vidPid: n.vidPid,
    details: details(n),
    internalCount: 0,
    carriesDisplay: false,
    children: tree.children.map(toView),
  };
  if (n.kind === 'hub' && tree.children.length > 0) {
    view.note = 'Downstream — draws power, supplies none to the host.';
  }
  return view;
}

/** Hubs routinely self-describe as "Generic"; a child sharing the hub's vendor
 *  identifies the hardware the hub lives in (the LG-monitor rule). */
function displayTitle(n: ContractNode, children: ContractNode[]): string {
  const vague = ['generic', '', 'unknown'];
  if (!vague.includes((n.vendorName ?? '').toLowerCase())) return n.name;
  const vid = n.vidPid?.slice(0, 4);
  if (!vid) return n.name;
  const sibling = children.find(
    (c) => c.vidPid?.slice(0, 4) === vid && !vague.includes((c.vendorName ?? '').toLowerCase()),
  );
  return sibling?.vendorName ? `${n.name} — ${sibling.vendorName}` : n.name;
}

function badges(n: ContractNode, childCount: number): string[] {
  const out: string[] = [];
  if (n.tb?.linkGbps) out.push(`${Math.round(n.tb.linkGbps)} Gb/s`);
  else if (n.linkBitsPerSecond) out.push(speedLabel(n.linkBitsPerSecond));
  if (childCount > 0 && n.kind === 'hub') out.push(`hub · ${childCount}`);
  return out;
}

function speedLabel(bps: number): string {
  return bps >= 1_000_000_000 ? `${bps / 1_000_000_000} Gb/s` : `${bps / 1_000_000} Mb/s`;
}

function details(n: ContractNode): Detail[] {
  const rows: Detail[] = [{ label: 'Name', value: n.name }];
  if (n.vendorName) rows.push({ label: 'Vendor', value: n.vendorName });
  if (n.vidPid) rows.push({ label: 'VID:PID', value: n.vidPid, searchable: true });
  if (n.usbClass !== undefined) rows.push({ label: 'Class', value: `0x${n.usbClass.toString(16).padStart(2, '0').toUpperCase()}${n.usbClass === 9 ? ' (hub)' : ''}` });
  if (n.linkBitsPerSecond) rows.push({ label: 'Link', value: speedLabel(n.linkBitsPerSecond) });
  if (n.tb) {
    if (n.tb.linkGbps) rows.push({ label: 'TB link', value: `${n.tb.linkGbps} Gb/s` });
    if (n.tb.routeString !== undefined) rows.push({ label: 'Route', value: String(n.tb.routeString) });
    if (n.tb.firmware) rows.push({ label: 'Firmware', value: n.tb.firmware });
  }
  if (n.builtIn !== undefined) rows.push({ label: 'Built-in', value: n.builtIn ? 'yes (integrated device)' : 'no' });
  rows.push({ label: 'Contract id', value: n.id });
  return rows;
}

function hostBadges(env: ContractEnvelope): string[] {
  const p = env.power;
  if (p.source === 'mains') return ['mains'];
  return [p.externalConnected ? 'on AC' : 'on battery'];
}

function powerNode(env: ContractEnvelope): ViewNode {
  const p = env.power;
  const base = {
    id: 'power',
    kind: 'power' as const,
    protocol: 'power' as const,
    tunneled: false,
    internalCount: 0,
    carriesDisplay: false,
    children: [] as ViewNode[],
    details: [] as Detail[],
  };
  if (p.source === 'mains') {
    return { ...base, title: 'Mains power', badges: [],
      note: 'Powered directly from the wall; no adapter or battery telemetry exists on this machine.' };
  }
  if (p.source === 'dock' || p.adapter?.identifiesItself === false) {
    return { ...base, title: 'Dock (unidentified supply)',
      badges: p.adapter?.watts ? [`${p.adapter.watts}W`] : [],
      note: 'Power is coming from the dock, over the same cable as your data.' };
  }
  if (!p.adapter?.watts) {
    return { ...base, title: 'No adapter', badges: [], note: 'Nothing is supplying power.' };
  }
  return { ...base, title: p.adapter.name ?? 'Power adapter',
    badges: [`${p.adapter.watts}W`],
    note: 'Power enters here, on its own cable — independent of the data link.' };
}

/** Displays merge into the box they belong to (brand match) rather than being
 *  drawn as a second copy of one physical monitor; display-only monitors hang
 *  off the nearest Thunderbolt node (their video carrier) or the host. */
function attachDisplays(env: ContractEnvelope, host: ViewNode): void {
  if (env.displaysKnown === false) return;
  for (const display of env.displays ?? []) {
    if (display.builtIn) continue;
    const brand = display.name.split(' ')[0];
    if (brand.length >= 2 && mergeDisplay(host, display, brand)) continue;
    const holder = findKind(host, 'thunderbolt') ?? host;
    holder.children.push({
      id: `display:${display.name}:${display.widthPx}x${display.heightPx}`,
      kind: 'display',
      title: display.name,
      badges: [
        `${display.widthPx} × ${display.heightPx}`,
        ...(display.refreshHz ? [`${Math.round(display.refreshHz)} Hz`] : []),
      ],
      protocol: 'displayPort',
      // Only genuinely tunneled when riding a Thunderbolt link.
      tunneled: holder.kind === 'thunderbolt',
      details: [
        { label: 'Display', value: display.name },
        { label: 'Resolution', value: `${display.widthPx} × ${display.heightPx}` },
      ],
      internalCount: 0,
      carriesDisplay: true,
      children: [],
    });
  }
}

function mergeDisplay(node: ViewNode, display: ContractDisplay, brand: string): boolean {
  if (
    node.title.toLowerCase().includes(brand.toLowerCase()) &&
    (node.kind === 'hub' || node.kind === 'device')
  ) {
    node.kind = 'display';
    node.title = display.name;
    node.carriesDisplay = true;
    node.badges.unshift(`${display.widthPx} × ${display.heightPx}`);
    node.details.push({ label: 'Resolution', value: `${display.widthPx} × ${display.heightPx}` });
    return true;
  }
  return node.children.some((c) => mergeDisplay(c, display, brand));
}

function findKind(node: ViewNode, kind: NodeKind): ViewNode | undefined {
  if (node.kind === kind) return node;
  for (const c of node.children) {
    const hit = findKind(c, kind);
    if (hit) return hit;
  }
  return undefined;
}

/** Physical mode: fold same-enclosure descendants and controller silicon into
 *  their box; Thunderbolt nodes are boxes by definition and never fold. */
function collapse(root: ViewNode, contract: ContractNode[]): ViewNode {
  const vendorById = new Map(contract.map((n) => [n.id, vendorKey(n)]));

  function physicalise(node: ViewNode): ViewNode {
    const myVendor = vendorById.get(node.id);
    let folded = node.internalCount;
    const surfaced: ViewNode[] = [];

    const gather = (current: ViewNode): void => {
      for (const child of current.children) {
        if (child.kind === 'thunderbolt' || child.kind === 'display') {
          surfaced.push(child);
          continue;
        }
        const childVendor = vendorById.get(child.id);
        const sameBox = myVendor !== undefined && childVendor === myVendor;
        const silicon = childVendor !== undefined && CONTROLLER_SILICON.has(childVendor);
        if (sameBox || silicon) {
          folded += 1 + child.internalCount;
          gather(child);
        } else {
          surfaced.push(child);
        }
      }
    };
    gather(node);

    const result: ViewNode = { ...node, internalCount: folded, children: surfaced.map(physicalise) };
    if (folded > 0) result.badges = [...result.badges, `+${folded} internal`];
    return result;
  }
  return physicalise(root);
}

/** Thunderbolt nodes get a badge stating what actually rides their link —
 *  once USB 2.0 stops dashing (it is native, not tunneled), this is how you
 *  still see at a glance that the link is carrying something. */
function summariseTunnels(node: ViewNode): void {
  node.children.forEach(summariseTunnels);
  if (node.kind !== 'thunderbolt') return;

  let usb3 = false;
  let usb2 = false;
  let displays = 0;
  const scan = (n: ViewNode): void => {
    for (const c of n.children) {
      if (c.protocol === 'usb3') usb3 = true;
      if (c.protocol === 'usb2' || c.protocol === 'usbLow') usb2 = true;
      if (c.kind === 'display' || c.carriesDisplay) displays += 1;
      scan(c);
    }
  };
  scan(node);

  const carried: string[] = [];
  if (displays > 0) carried.push(displays === 1 ? 'DP' : `DP ×${displays}`);
  if (usb3) carried.push('USB3');
  if (carried.length > 0) node.badges.push(`tunnels: ${carried.join(' + ')}`);
  if (usb2) node.badges.push('USB2 native');
}
