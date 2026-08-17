/**
 * Ingest for Connection Contract v1: validation, normalization, and the
 * tolerances the contract mandates (unknown fields ignored, optional fields
 * absent, corrupt JSONL lines skipped-and-counted).
 */

import {
  CONTRACT_SCHEMA,
  type ContractAnalysis,
  type ContractEnvelope,
  type ContractEvent,
  type ContractFinding,
  type ContractIncident,
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
    // Optional analysis block (contract-findings-incidents). Absent stays
    // absent — that is the "no recording" signal — but present-and-invalid
    // is an error, not silently dropped: a finding without evidence, or an
    // unknown severity, is a producer bug the reader should hear about.
    findings: doc.findings === undefined
      ? undefined
      : asArray(doc.findings, 'findings').map((f, i) => {
          try {
            return parseFinding(f);
          } catch (cause) {
            throw new ContractError(`findings[${i}]: ${(cause as Error).message}`);
          }
        }),
    incidents: doc.incidents === undefined
      ? undefined
      : asArray(doc.incidents, 'incidents').map((inc, i) => parseIncident(inc, i)),
    analysis: doc.analysis === undefined ? undefined : parseAnalysis(doc.analysis),
  };
}

export function parseIncident(json: unknown, index = 0): ContractIncident {
  const doc = asObject(json, `incidents[${index}]`);
  const lost = Array.isArray(doc.devicesLost)
    ? doc.devicesLost.map((d, j) => {
        const device = asObject(d, `incidents[${index}].devicesLost[${j}]`);
        return { name: asString(device.name, `incidents[${index}].devicesLost[${j}].name`),
          vidPid: optString(device.vidPid)?.toUpperCase() };
      })
    : undefined;
  const power = doc.power && typeof doc.power === 'object'
    ? { peakDischargeMilliwatts: optNumber((doc.power as Record<string, unknown>).peakDischargeMilliwatts) }
    : undefined;
  return {
    start: asString(doc.start, `incidents[${index}].start`),
    end: optString(doc.end),
    rootEvent: asOptionalEventKind(doc.rootEvent, `incidents[${index}].rootEvent`),
    devicesLost: lost,
    sharedParent: optString(doc.sharedParent),
    power,
  };
}

export function parseAnalysis(json: unknown): ContractAnalysis {
  const doc = asObject(json, 'analysis');
  const coverage = asObject(doc.coverage, 'analysis.coverage');
  const baseline = doc.baseline && typeof doc.baseline === 'object'
    ? (doc.baseline as ContractAnalysis['baseline'])
    : undefined;
  if (typeof coverage.complete !== 'boolean') {
    // "false" would be truthy — the one field that must never be coerced.
    throw new ContractError('analysis.coverage.complete must be a boolean');
  }
  if (typeof doc.windowHours !== 'number' || !Number.isFinite(doc.windowHours)) {
    throw new ContractError('analysis.windowHours must be a number');
  }
  if (baseline && !['no-baseline', 'healthy', 'active-fault', 'recovered'].includes(String(baseline.state))) {
    throw new ContractError(`analysis.baseline.state invalid: ${JSON.stringify(baseline.state)}`);
  }
  return {
    windowHours: doc.windowHours,
    generatedAt: asString(doc.generatedAt, 'analysis.generatedAt'),
    coverage: {
      availableFrom: asString(coverage.availableFrom, 'analysis.coverage.availableFrom'),
      through: asString(coverage.through, 'analysis.coverage.through'),
      complete: coverage.complete,
      reasons: coverage.reasons === undefined
        ? undefined
        : asArray(coverage.reasons, 'analysis.coverage.reasons').map((r, i) => asString(r, `analysis.coverage.reasons[${i}]`)),
    },
    baseline,
    capabilities: doc.capabilities === undefined ? undefined : parseCapabilities(doc.capabilities),
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
    if (event.kind === 'fullSnapshot') {
      // A sync point is a *complete* envelope. One without a snapshot, or with
      // an invalid one, is not a sync point — count it as skipped so the host
      // reads as history-incomplete instead of silently losing its anchor.
      if (!event.snapshot) {
        skippedLines += 1;
        continue;
      }
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
    // A finding without a recommendation tells the reader what is wrong and
    // leaves them there; the contract makes it required.
    recommendation: asString(doc.recommendation, 'finding.recommendation'),
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
    builtIn: typeof doc.builtIn === 'boolean' ? doc.builtIn : undefined,
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
function asOptionalEventKind(v: unknown, label: string): ContractIncident['rootEvent'] {
  if (v === undefined) return undefined;
  if (typeof v !== 'string' || !EVENT_KINDS.has(v)) throw new ContractError(`${label} invalid: ${JSON.stringify(v)}`);
  return v as ContractIncident['rootEvent'];
}
const LINK_EVENT_CAPABILITIES = new Set(['kernel', 'notification', 'poll', 'unavailable']);
const BASELINE_CAPABILITIES = new Set(['available', 'busy', 'unreadable', 'history-unreadable', 'history-unwritable']);

/** Strict on known fields: a capability we cannot read is a producer bug the
 *  reader should hear about, not a value the UI then formats and crashes on. */
function parseCapabilities(json: unknown): ContractAnalysis['capabilities'] {
  const doc = asObject(json, 'analysis.capabilities');
  const linkEvents = doc.linkEvents;
  if (linkEvents !== undefined && (typeof linkEvents !== 'string' || !LINK_EVENT_CAPABILITIES.has(linkEvents))) {
    throw new ContractError(`analysis.capabilities.linkEvents invalid: ${JSON.stringify(linkEvents)}`);
  }
  const baseline = doc.baseline;
  if (baseline !== undefined && (typeof baseline !== 'string' || !BASELINE_CAPABILITIES.has(baseline))) {
    throw new ContractError(`analysis.capabilities.baseline invalid: ${JSON.stringify(baseline)}`);
  }
  return {
    linkEvents: linkEvents as NonNullable<ContractAnalysis['capabilities']>['linkEvents'],
    baseline: baseline as NonNullable<ContractAnalysis['capabilities']>['baseline'],
  };
}

function optNumber(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}
function asPowerSource(v: unknown): ContractEnvelope['power']['source'] {
  return v === 'dock' || v === 'battery' || v === 'mains' ? v : 'adapter';
}
