import { useMemo, useState } from 'react';
import type { ContractEnvelope, LinkProtocol, NodeKind } from '../contract/types';
import { buildTopology, builtInChip, modeChip, type TopoMode, type ViewNode } from '../domain/topology';
import { layoutDiagram, NODE_H, type DiagramStyle } from '../domain/layout';
import { Inspector } from './Inspector';

export const PROTOCOL_COLOR: Record<LinkProtocol, string> = {
  power: '#e5c04b',
  thunderbolt: '#b07cf0',
  displayPort: '#f06292',
  usb3: '#5a9cf8',
  usb2: '#4cc2c4',
  usbLow: '#8b8f98',
  unknown: '#6f7078',
};

export const KIND_COLOR: Record<NodeKind, string> = {
  power: '#e5c04b',
  host: '#5a9cf8',
  thunderbolt: '#b07cf0',
  hub: '#f0913c',
  device: '#4cc2c4',
  display: '#f06292',
};

const LEGEND: Array<[string, string]> = [
  ['power source', KIND_COLOR.power],
  ['host', KIND_COLOR.host],
  ['Thunderbolt', KIND_COLOR.thunderbolt],
  ['hub (consumer)', KIND_COLOR.hub],
  ['device', KIND_COLOR.device],
  ['display', KIND_COLOR.display],
];

export function TopologyView({ envelope, recordedLabel }: {
  envelope: ContractEnvelope;
  recordedLabel: string;
}) {
  const [style, setStyle] = useState<DiagramStyle>(
    () => (localStorage.getItem('diagramStyle') as DiagramStyle) ?? 'cascade',
  );
  const [mode, setMode] = useState<TopoMode>(
    () => (localStorage.getItem('diagramMode') as TopoMode) ?? 'physical',
  );
  const [includeBuiltIn, setIncludeBuiltIn] = useState<boolean>(
    () => localStorage.getItem('includeBuiltIn') === 'true',
  );
  const [selectedId, setSelectedId] = useState<string>();

  const topology = useMemo(
    () => buildTopology(envelope, mode, { includeBuiltIn }),
    [envelope, mode, includeBuiltIn],
  );
  const layout = useMemo(() => layoutDiagram(topology.root, style), [topology, style]);
  const builtInText = builtInChip(topology.stats);
  const selected = layout.nodes.find((n) => n.id === selectedId)?.node;

  return (
    <div className="topology">
      <div className="toolbar">
        <Segmented options={['cascade', 'topDown', 'flow'] as const} value={style}
          labels={{ cascade: 'Cascade', topDown: 'Top-down', flow: 'Flow' }}
          onChange={(v) => { localStorage.setItem('diagramStyle', v); setStyle(v); }} />
        <Segmented options={['physical', 'full'] as const} value={mode}
          labels={{ physical: 'Physical', full: 'All device nodes' }}
          onChange={(v) => { localStorage.setItem('diagramMode', v); setMode(v); }} />
        <span className="chip" data-testid="mode-chip" role="status" aria-live="polite">{modeChip(mode, topology.stats)}</span>
        <label className="toggle">
          <input type="checkbox" checked={includeBuiltIn}
            onChange={(e) => { localStorage.setItem('includeBuiltIn', String(e.target.checked)); setIncludeBuiltIn(e.target.checked); }} />
          Include built-in devices
        </label>
        {builtInText && <span className="chip muted" data-testid="builtin-chip" role="status" aria-live="polite">{builtInText}</span>}
        <span className="spacer" />
        <span className="recorded">{recordedLabel}</span>
      </div>

      <div className="canvas-row">
        <div className="canvas">
          <svg width={layout.width} height={layout.height} role="img" aria-label="connection diagram">
            {layout.edges.map((e) => (
              <polyline key={e.id}
                points={e.points.map((p) => `${p.x},${p.y}`).join(' ')}
                fill="none"
                stroke={PROTOCOL_COLOR[e.protocol]}
                strokeWidth={e.protocol === 'power' || e.protocol === 'thunderbolt' || e.protocol === 'displayPort' ? 2.4 : 1.5}
                strokeDasharray={e.tunneled ? '5 3' : undefined}
                strokeLinejoin="round" opacity={0.9} />
            ))}
            {layout.nodes.map((p) => (
              <NodeBox key={p.id} placed={p} selected={p.id === selectedId}
                onClick={() => setSelectedId(p.id === selectedId ? undefined : p.id)} />
            ))}
          </svg>
        </div>
        {selected && <Inspector node={selected} onClose={() => setSelectedId(undefined)} />}
      </div>

      <div className="legend">
        {LEGEND.map(([label, color]) => (
          <span key={label} className="legend-item">
            <i style={{ background: color }} /> {label}
          </span>
        ))}
        <span className="legend-item"><i className="dash" /> tunneled (USB 2.0 is native, never dashed)</span>
      </div>
    </div>
  );
}

function NodeBox({ placed, selected, onClick }: {
  placed: { frame: { x: number; y: number; w: number; h: number }; node: ViewNode };
  selected: boolean;
  onClick: () => void;
}) {
  const { frame, node } = placed;
  const color = KIND_COLOR[node.kind];
  return (
    <g transform={`translate(${frame.x},${frame.y})`} onClick={onClick} style={{ cursor: 'pointer' }}>
      <rect width={frame.w} height={NODE_H} rx={9}
        fill={color} fillOpacity={node.kind === 'device' ? 0.07 : 0.13}
        stroke={color} strokeOpacity={selected ? 1 : 0.45} strokeWidth={selected ? 2.5 : 1.2} />
      <rect x={10} y={NODE_H / 2 - 9} width={6} height={18} rx={3} fill={color} />
      <text x={26} y={NODE_H / 2 - 4} fill="var(--text)" fontSize={12.5}
        fontWeight={node.kind === 'device' ? 400 : 600}>{node.title}</text>
      <text x={26} y={NODE_H / 2 + 13} fill={color} fontSize={10.5}>
        {node.badges.join('   ')}
      </text>
    </g>
  );
}

function Segmented<T extends string>({ options, value, labels, onChange }: {
  options: readonly T[];
  value: T;
  labels: Record<T, string>;
  onChange: (v: T) => void;
}) {
  return (
    <div className="segmented" role="radiogroup">
      {options.map((option) => (
        <button key={option} role="radio" aria-checked={option === value}
          className={option === value ? 'on' : ''} onClick={() => onChange(option)}>
          {labels[option]}
        </button>
      ))}
    </div>
  );
}
