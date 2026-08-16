/**
 * Diagram placement: port of TBDoctor's Diagram engine. Three layouts, boxes
 * sized to their text, orthogonal edges, and secondary DisplayPort edges
 * routed outside the tree's footprint (the rule that fixed the unreadable
 * routing in the native app: leave the footprint, travel, come back in).
 */

import type { LinkProtocol } from '../contract/types';
import type { ViewNode } from './topology';

export type DiagramStyle = 'cascade' | 'topDown' | 'flow';

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface PlacedNode {
  id: string;
  node: ViewNode;
  frame: Rect;
}

export interface DiagramEdge {
  id: string;
  points: Array<{ x: number; y: number }>;
  protocol: LinkProtocol;
  tunneled: boolean;
}

export interface DiagramLayout {
  nodes: PlacedNode[];
  edges: DiagramEdge[];
  width: number;
  height: number;
}

export const NODE_H = 56;
const MIN_W = 150;
const MAX_W = 380;
const LEADING = 45; // icon tile + padding, matched to the component
const TRAILING = 22;
const TITLE_CHAR_W = 7.2; // deterministic approximation of 12.5px system font
const BADGE_CHAR_W = 6.0;
const MARGIN = 24;

/** Boxes size to their text: truncating "4-Port USB 2.0 Hub — LG Electronics
 *  Inc." throws away the identifying word. */
export function nodeWidth(node: ViewNode): number {
  const title = node.title.length * TITLE_CHAR_W;
  const badgeText = node.badges.join('   ').length * BADGE_CHAR_W;
  return Math.min(MAX_W, Math.max(MIN_W, LEADING + Math.max(title, badgeText) + TRAILING));
}

export function layoutDiagram(root: ViewNode, style: DiagramStyle): DiagramLayout {
  const layout =
    style === 'cascade' ? cascade(root) : style === 'topDown' ? topDown(root) : flow(root);
  addDisplayLinks(layout, root, style);
  return finish(layout);
}

const mid = (r: Rect) => ({ cx: r.x + r.w / 2, cy: r.y + r.h / 2 });

function edge(id: string, node: ViewNode, points: DiagramEdge['points']): DiagramEdge {
  return { id, points, protocol: node.protocol, tunneled: node.tunneled };
}

// --- cascade: each child steps down and right; narrow, grows downward ---

function cascade(root: ViewNode): DiagramLayout {
  const INDENT = 44;
  const VGAP = 18;
  const STEM = 18;
  const nodes: PlacedNode[] = [];
  const edges: DiagramEdge[] = [];
  let y = 0;

  const walk = (node: ViewNode, depth: number, parent?: Rect): void => {
    const frame: Rect = { x: depth * INDENT, y, w: nodeWidth(node), h: NODE_H };
    y += NODE_H + VGAP;
    nodes.push({ id: node.id, node, frame });
    if (parent) {
      const x = parent.x + STEM;
      edges.push(
        edge(node.id, node, [
          { x, y: parent.y + parent.h },
          { x, y: mid(frame).cy },
          { x: frame.x, y: mid(frame).cy },
        ]),
      );
    }
    node.children.forEach((c) => walk(c, depth + 1, frame));
  };
  walk(root, 0);
  return { nodes, edges, width: 0, height: 0 };
}

// --- topDown: children fan out below; power enters from the left ---

function topDown(root: ViewNode): DiagramLayout {
  const HGAP = 20;
  const VGAP = 48;
  const POWER_GAP = 64;
  const nodes: PlacedNode[] = [];
  const edges: DiagramEdge[] = [];
  const host = root.children[0];
  if (!host) return single(root);

  let cursor = 0;
  const links: Array<{ child: ViewNode; parent: Rect; frame: Rect }> = [];

  const place = (node: ViewNode, depth: number): Rect => {
    const w = nodeWidth(node);
    const yy = depth * (NODE_H + VGAP);
    if (node.children.length === 0) {
      const frame: Rect = { x: cursor, y: yy, w, h: NODE_H };
      cursor += w + HGAP;
      nodes.push({ id: node.id, node, frame });
      return frame;
    }
    const childFrames = node.children.map((c) => place(c, depth + 1));
    const first = mid(childFrames[0]).cx;
    const last = mid(childFrames[childFrames.length - 1]).cx;
    const frame: Rect = { x: (first + last) / 2 - w / 2, y: yy, w, h: NODE_H };
    nodes.push({ id: node.id, node, frame });
    node.children.forEach((c, i) => links.push({ child: c, parent: frame, frame: childFrames[i] }));
    return frame;
  };

  const hostFrame = place(host, 0);
  // Directly beside the host — left of the whole tree stretches the power edge
  // across the diagram and pushes the host off screen (tried, reverted).
  const pw = nodeWidth(root);
  const powerFrame: Rect = { x: hostFrame.x - pw - POWER_GAP, y: hostFrame.y, w: pw, h: NODE_H };
  nodes.push({ id: root.id, node: root, frame: powerFrame });
  edges.push(
    edge('power', root, [
      { x: powerFrame.x + powerFrame.w, y: mid(powerFrame).cy },
      { x: hostFrame.x, y: mid(hostFrame).cy },
    ]),
  );

  for (const { child, parent, frame } of links) {
    const midY = parent.y + parent.h + VGAP / 2;
    edges.push(
      edge(child.id, child, [
        { x: mid(parent).cx, y: parent.y + parent.h },
        { x: mid(parent).cx, y: midY },
        { x: mid(frame).cx, y: midY },
        { x: mid(frame).cx, y: frame.y },
      ]),
    );
  }
  return { nodes, edges, width: 0, height: 0 };
}

// --- flow: left to right; columns as wide as their widest member ---

function flow(root: ViewNode): DiagramLayout {
  const HGAP = 62;
  const VGAP = 14;
  const nodes: PlacedNode[] = [];
  const edges: DiagramEdge[] = [];

  const widest = new Map<number, number>();
  const measure = (n: ViewNode, d: number): void => {
    widest.set(d, Math.max(widest.get(d) ?? 0, nodeWidth(n)));
    n.children.forEach((c) => measure(c, d + 1));
  };
  measure(root, 0);

  const columnX = new Map<number, number>();
  let x = 0;
  for (const depth of [...widest.keys()].sort((a, b) => a - b)) {
    columnX.set(depth, x);
    x += (widest.get(depth) ?? 0) + HGAP;
  }

  let cursor = 0;
  const place = (node: ViewNode, depth: number): Rect => {
    const w = nodeWidth(node);
    const xx = columnX.get(depth) ?? 0;
    if (node.children.length === 0) {
      const frame: Rect = { x: xx, y: cursor, w, h: NODE_H };
      cursor += NODE_H + VGAP;
      nodes.push({ id: node.id, node, frame });
      return frame;
    }
    const childFrames = node.children.map((c) => place(c, depth + 1));
    const first = mid(childFrames[0]).cy;
    const last = mid(childFrames[childFrames.length - 1]).cy;
    const frame: Rect = { x: xx, y: (first + last) / 2 - NODE_H / 2, w, h: NODE_H };
    nodes.push({ id: node.id, node, frame });
    node.children.forEach((c, i) => {
      const cf = childFrames[i];
      const midX = frame.x + frame.w + HGAP / 2;
      edges.push(
        edge(c.id, c, [
          { x: frame.x + frame.w, y: mid(frame).cy },
          { x: midX, y: mid(frame).cy },
          { x: midX, y: mid(cf).cy },
          { x: cf.x, y: mid(cf).cy },
        ]),
      );
    });
    return frame;
  };
  place(root, 0);
  return { nodes, edges, width: 0, height: 0 };
}

function single(root: ViewNode): DiagramLayout {
  return {
    nodes: [{ id: root.id, node: root, frame: { x: 0, y: 0, w: nodeWidth(root), h: NODE_H } }],
    edges: [],
    width: 0,
    height: 0,
  };
}

/** A monitor with a hub has two connections; a tree expresses one. The second
 *  (DisplayPort) edge routes OUTSIDE the tree's footprint per style. */
function addDisplayLinks(layout: DiagramLayout, root: ViewNode, style: DiagramStyle): void {
  const nearestDock = new Map<string, string>();
  const walk = (n: ViewNode, dock?: string): void => {
    if (dock) nearestDock.set(n.id, dock);
    const next = n.kind === 'thunderbolt' ? n.id : dock;
    n.children.forEach((c) => walk(c, next));
  };
  walk(root);

  const frames = new Map(layout.nodes.map((p) => [p.id, p.frame]));
  const minX = Math.min(...layout.nodes.map((p) => p.frame.x));
  const maxX = Math.max(...layout.nodes.map((p) => p.frame.x + p.frame.w));
  const minY = Math.min(...layout.nodes.map((p) => p.frame.y));
  const maxY = Math.max(...layout.nodes.map((p) => p.frame.y + p.frame.h));

  for (const placed of layout.nodes) {
    if (!placed.node.carriesDisplay || placed.node.protocol === 'displayPort') continue;
    const dockId = nearestDock.get(placed.id);
    const source = dockId ? frames.get(dockId) : undefined;
    if (!source) continue;
    const target = placed.frame;
    const dp: DiagramEdge = { id: `dp-${placed.id}`, points: [], protocol: 'displayPort', tunneled: true };

    if (style === 'cascade') {
      const lane = Math.max(source.x + source.w, target.x + target.w) + 24;
      dp.points = [
        { x: source.x + source.w, y: mid(source).cy },
        { x: lane, y: mid(source).cy },
        { x: lane, y: mid(target).cy },
        { x: target.x + target.w, y: mid(target).cy },
      ];
    } else if (style === 'topDown') {
      const goLeft = mid(target).cx < mid(source).cx;
      const lane = goLeft ? minX - 18 : maxX + 18;
      dp.points = [
        { x: goLeft ? source.x : source.x + source.w, y: mid(source).cy },
        { x: lane, y: mid(source).cy },
        { x: lane, y: mid(target).cy },
        { x: goLeft ? target.x : target.x + target.w, y: mid(target).cy },
      ];
    } else {
      const lane = minY - 18;
      dp.points = [
        { x: mid(source).cx, y: source.y },
        { x: mid(source).cx, y: lane },
        { x: mid(target).cx, y: lane },
        { x: mid(target).cx, y: target.y },
      ];
    }
    layout.edges.push(dp);
  }
  void maxY;
}

/** Shift everything to the origin with a margin; record total size. */
function finish(layout: DiagramLayout): DiagramLayout {
  const xs = [
    ...layout.nodes.map((p) => p.frame.x),
    ...layout.edges.flatMap((e) => e.points.map((p) => p.x)),
  ];
  const ys = [
    ...layout.nodes.map((p) => p.frame.y),
    ...layout.edges.flatMap((e) => e.points.map((p) => p.y)),
  ];
  const dx = MARGIN - Math.min(...xs, 0);
  const dy = MARGIN - Math.min(...ys, 0);

  for (const p of layout.nodes) {
    p.frame = { ...p.frame, x: p.frame.x + dx, y: p.frame.y + dy };
  }
  for (const e of layout.edges) {
    e.points = e.points.map((pt) => ({ x: pt.x + dx, y: pt.y + dy }));
  }
  layout.width = Math.max(...layout.nodes.map((p) => p.frame.x + p.frame.w)) + MARGIN;
  layout.height = Math.max(...layout.nodes.map((p) => p.frame.y + p.frame.h)) + MARGIN;
  return layout;
}
