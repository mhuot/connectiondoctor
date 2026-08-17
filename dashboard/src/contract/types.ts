/**
 * Connection Contract v1 — typed model.
 *
 * Canonical spec: docs/schema-v1.md at the repo root. Additive-only within v1;
 * consumers tolerate unknown fields, so every interface here is open to
 * extension and parsing never rejects extra keys.
 */

export const CONTRACT_SCHEMA = 'connection-contract/v1';

/** Deficit threshold shared by both producers (milliwatts, discharge negative). */
export const DEFICIT_THRESHOLD_MW = -2000;

export type HostOS = 'macos' | 'windows';

export interface ContractHost {
  name: string;
  os: HostOS;
  arch: string;
  model?: string;
}

export type PowerSource = 'adapter' | 'dock' | 'battery' | 'mains';

export interface ContractAdapter {
  watts?: number;
  name?: string;
  vendor?: string;
  identifiesItself?: boolean;
  serial?: string;
}

export interface ContractPower {
  source: PowerSource;
  externalConnected: boolean;
  batteryPresent: boolean;
  batteryPercent?: number;
  batteryRateMilliwatts?: number;
  adapter?: ContractAdapter;
}

export type NodeKind =
  | 'host'
  | 'thunderbolt'
  | 'hub'
  | 'device'
  | 'display'
  | 'power';

export type LinkProtocol =
  | 'power'
  | 'thunderbolt'
  | 'displayPort'
  | 'usb3'
  | 'usb2'
  | 'usbLow'
  | 'unknown';

export interface ContractNode {
  id: string;
  parentId?: string;
  kind: NodeKind;
  name: string;
  vendorName?: string;
  /** Uppercase hex "VVVV:PPPP" — the cross-platform identity. */
  vidPid?: string;
  protocol: LinkProtocol;
  linkBitsPerSecond?: number;
  /** Only for what USB4 genuinely tunnels (DP/USB3/PCIe); USB 2.0 is native. */
  tunneled?: boolean;
  usbClass?: number;
  /** Producer classification of integrated devices (internal keyboard,
   *  trackpad, camera, the built-in display's hub). Absent = unknown, never
   *  "external"; consumers filter on it as a view choice, never infer it. */
  builtIn?: boolean;
  tb?: {
    routeString?: number | string;
    depth?: number;
    linkGbps?: number;
    firmware?: string;
  };
  /** Untranslated native identifiers; consumers must not depend on these. */
  platform?: Record<string, unknown>;
}

export interface ContractDisplay {
  name: string;
  widthPx: number;
  heightPx: number;
  refreshHz?: number;
  builtIn: boolean;
  attachedTo?: string;
}

export interface ContractEnvelope {
  schema: typeof CONTRACT_SCHEMA;
  capturedAt: string;
  host: ContractHost;
  power: ContractPower;
  nodes: ContractNode[];
  displays?: ContractDisplay[];
  displaysKnown?: boolean;
  /** Present only when the collector has recorded history. Absent ≠ empty:
   *  a consumer can tell "nothing found" from "nothing recorded". */
  findings?: ContractFinding[];
  incidents?: ContractIncident[];
  analysis?: ContractAnalysis;
}

/** What the recorder can vouch for. `complete` is true only when the recording
 *  spans the whole requested window with no trim inside it and no gap longer
 *  than 3× the sample interval; `reasons` are temporal only. */
export interface ContractCoverage {
  availableFrom: string;
  through: string;
  complete: boolean;
  reasons?: string[];
}

export type BaselineState = 'no-baseline' | 'healthy' | 'active-fault' | 'recovered';

export interface ContractAnalysis {
  windowHours: number;
  generatedAt: string;
  coverage: ContractCoverage;
  baseline?: { state: BaselineState; capturedAt?: string; faultSince?: string; recoveredAt?: string };
  /** What the collector can attribute — a separate axis from coverage. */
  capabilities?: { linkEvents?: 'kernel' | 'notification' | 'poll' | 'unavailable' };
}

export type EventKind =
  | 'linkDown'
  | 'linkUp'
  | 'deviceAdded'
  | 'deviceRemoved'
  | 'adapterChanged'
  | 'deficitStart'
  | 'deficitEnd'
  | 'portError'
  | 'fullSnapshot';

/** Event kinds that identify a fault's origin rather than its fallout. */
export const ROOT_EVENT_KINDS: ReadonlySet<EventKind> = new Set(['linkDown']);

export interface ContractEvent {
  t: string;
  kind: EventKind;
  nodeId?: string;
  vidPid?: string;
  name?: string;
  /** Present on fullSnapshot events: a complete envelope as a sync point. */
  snapshot?: ContractEnvelope;
}

export type FindingSeverity = 'info' | 'warning' | 'critical';

export interface ContractFinding {
  severity: FindingSeverity;
  title: string;
  explanation: string;
  /** Mandatory and non-empty: a verdict you cannot audit is an opinion. */
  evidence: string[];
  /** Mandatory: a finding that leaves the reader without a next step is half a finding. */
  recommendation: string;
  confidence?: string;
}

export interface ContractIncident {
  start: string;
  end?: string;
  rootEvent?: EventKind;
  devicesLost?: Array<{ vidPid?: string; name: string }>;
  sharedParent?: string;
  power?: { peakDischargeMilliwatts?: number };
}

export function isDeficit(power: ContractPower): boolean {
  return (
    power.externalConnected &&
    power.batteryRateMilliwatts !== undefined &&
    power.batteryRateMilliwatts <= DEFICIT_THRESHOLD_MW
  );
}
