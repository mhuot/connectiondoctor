/**
 * Ingest for Connection Contract v1: validation, normalization, and the
 * tolerances the contract mandates (unknown fields ignored, optional fields
 * absent, corrupt JSONL lines skipped-and-counted).
 */

import {
  CONTRACT_SCHEMA,
  type ContractEnvelope,
  type ContractEvent,
  type ContractFinding,
  type ContractNode,
} from './types';

export class ContractError extends Error {}

/** Parse one envelope document. Throws ContractError; never returns partial data. */
export function parseEnvelope(json: unknown): ContractEnvelope {
  const doc = asObject(json, 'envelope');

  if (doc.schema !== CONTRACT_SCHEMA) {
    throw new ContractError(
      `expected schema "${CONTRACT_SCHEMA}", got ${JSON.stringify(doc.schema ?? null)}`,
    );
  }
  const capturedAt = asString(doc.capturedAt, 'capturedAt');
  const host = asObject(doc.host, 'host');
  const power = asObject(doc.power, 'power');
  const nodes = asArray(doc.nodes, 'nodes').map((n, i) => normalizeNode(n, i));

  ensureUniqueIds(nodes);

  return {
    ...(doc as object),
    schema: CONTRACT_SCHEMA,
    capturedAt,
    host: {
      name: asString(host.name, 'host.name'),
      os: host.os === 'windows' ? 'windows' : 'macos',
      arch: asString(host.arch, 'host.arch'),
      model: optString(host.model),
    },
    power: {
      source: asPowerSource(power.source),
      externalConnected: Boolean(power.externalConnected),
      batteryPresent: Boolean(power.batteryPresent),
      batteryPercent: optNumber(power.batteryPercent),
      batteryRateMilliwatts: optNumber(power.batteryRateMilliwatts),
      adapter: power.adapter ? (power.adapter as ContractEnvelope['power']['adapter']) : undefined,
    },
    nodes,
    displays: Array.isArray(doc.displays)
      ? (doc.displays as ContractEnvelope['displays'])
      : undefined,
    displaysKnown: doc.displaysKnown === undefined ? undefined : Boolean(doc.displaysKnown),
  };
}

export interface TreeNode {
  node: ContractNode;
  children: TreeNode[];
}

export interface TreeResult {
  roots: TreeNode[];
  /** Nodes whose parentId resolved to nothing — attached at root, flagged, never dropped. */
  orphanIds: string[];
}

/** Hierarchy comes from id/parentId alone — the contract's core rule. */
export function buildTree(nodes: ContractNode[]): TreeResult {
  const byId = new Map(nodes.map((n) => [n.id, { node: n, children: [] as TreeNode[] }]));
  const roots: TreeNode[] = [];
  const orphanIds: string[] = [];

  for (const entry of byId.values()) {
    const parentId = entry.node.parentId;
    if (parentId === undefined) {
      roots.push(entry);
      continue;
    }
    const parent = byId.get(parentId);
    if (parent && parent !== entry) {
      parent.children.push(entry);
    } else {
      orphanIds.push(entry.node.id);
      roots.push(entry);
    }
  }
  return { roots, orphanIds };
}

export interface EventStreamResult {
  events: ContractEvent[];
  /** Lines that failed to parse — surfaced, never silently swallowed. */
  skippedLines: number;
  /** Index into `events` of the last fullSnapshot, if any: the sync point
   *  after which accumulated state is authoritative. */
  lastSnapshotIndex: number | null;
}

const EVENT_KINDS = new Set([
  'linkDown', 'linkUp', 'deviceAdded', 'deviceRemoved', 'adapterChanged',
  'deficitStart', 'deficitEnd', 'portError', 'fullSnapshot',
]);

export function parseEventStream(jsonl: string): EventStreamResult {
  const events: ContractEvent[] = [];
  let skippedLines = 0;
  let lastSnapshotIndex: number | null = null;

  for (const line of jsonl.split('\n')) {
    if (line.trim() === '') continue;
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      skippedLines += 1;
      continue;
    }
    const obj = parsed as Record<string, unknown>;
    if (typeof obj?.t !== 'string' || !EVENT_KINDS.has(obj.kind as string)) {
      skippedLines += 1;
      continue;
    }
    const event = obj as unknown as ContractEvent;
    if (event.kind === 'fullSnapshot' && event.snapshot) {
      try {
        event.snapshot = parseEnvelope(event.snapshot);
        lastSnapshotIndex = events.length;
      } catch {
        skippedLines += 1;
        continue;
      }
    }
    events.push(event);
  }
  return { events, skippedLines, lastSnapshotIndex };
}

/** Findings without evidence are rejected at ingest, per the contract. */
export function parseFinding(json: unknown): ContractFinding {
  const doc = asObject(json, 'finding');
  const evidence = asArray(doc.evidence, 'finding.evidence').map((e, i) =>
    asString(e, `finding.evidence[${i}]`),
  );
  if (evidence.length === 0) {
    throw new ContractError('finding has no evidence; a verdict without evidence is an opinion');
  }
  const severity = doc.severity;
  if (severity !== 'info' && severity !== 'warning' && severity !== 'critical') {
    throw new ContractError(`finding.severity invalid: ${JSON.stringify(severity)}`);
  }
  return {
    severity,
    title: asString(doc.title, 'finding.title'),
    explanation: asString(doc.explanation, 'finding.explanation'),
    evidence,
    recommendation: optString(doc.recommendation),
    confidence: optString(doc.confidence),
  };
}

// --- helpers ---

const NODE_KINDS = new Set(['host', 'thunderbolt', 'hub', 'device', 'display', 'power']);
const PROTOCOLS = new Set(['power', 'thunderbolt', 'displayPort', 'usb3', 'usb2', 'usbLow', 'unknown']);

function normalizeNode(json: unknown, index: number): ContractNode {
  const doc = asObject(json, `nodes[${index}]`);
  const kind = NODE_KINDS.has(doc.kind as string) ? (doc.kind as ContractNode['kind']) : 'device';
  const protocol = PROTOCOLS.has(doc.protocol as string)
    ? (doc.protocol as ContractNode['protocol'])
    : 'unknown';
  return {
    ...(doc as object),
    id: asString(doc.id, `nodes[${index}].id`),
    parentId: optString(doc.parentId),
    kind,
    name: asString(doc.name, `nodes[${index}].name`),
    vidPid: optString(doc.vidPid)?.toUpperCase(),
    protocol,
    // USB 2.0 is carried natively, never tunneled — enforce rather than trust.
    tunneled:
      (protocol === 'usb2' || protocol === 'usbLow') ? false : Boolean(doc.tunneled),
  };
}

function ensureUniqueIds(nodes: ContractNode[]): void {
  const seen = new Set<string>();
  for (const n of nodes) {
    if (seen.has(n.id)) throw new ContractError(`duplicate node id: ${n.id}`);
    seen.add(n.id);
  }
}

function asObject(v: unknown, label: string): Record<string, unknown> {
  if (typeof v !== 'object' || v === null || Array.isArray(v)) {
    throw new ContractError(`${label} must be an object`);
  }
  return v as Record<string, unknown>;
}
function asArray(v: unknown, label: string): unknown[] {
  if (!Array.isArray(v)) throw new ContractError(`${label} must be an array`);
  return v;
}
function asString(v: unknown, label: string): string {
  if (typeof v !== 'string' || v === '') throw new ContractError(`${label} must be a non-empty string`);
  return v;
}
function optString(v: unknown): string | undefined {
  return typeof v === 'string' && v !== '' ? v : undefined;
}
function optNumber(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}
function asPowerSource(v: unknown): ContractEnvelope['power']['source'] {
  return v === 'dock' || v === 'battery' || v === 'mains' ? v : 'adapter';
}
