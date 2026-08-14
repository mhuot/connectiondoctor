import type { ViewNode } from '../domain/topology';

/** Everything known about one node; each row copyable; VID:PID looks up in a
 *  public USB ID database — the identifier that names hardware whose own name
 *  names nothing. */
export function Inspector({ node, onClose }: { node: ViewNode; onClose: () => void }) {
  return (
    <aside className="inspector">
      <header>
        <strong>{node.title}</strong>
        <button onClick={onClose} aria-label="close">×</button>
      </header>
      {node.note && <p className="note">{node.note}</p>}
      <dl>
        {node.details.map((d) => (
          <div key={d.label} className="row">
            <dt>{d.label}</dt>
            <dd>
              <code>{d.value}</code>
              <button title="Copy" onClick={() => void navigator.clipboard.writeText(d.value)}>⧉</button>
            </dd>
          </div>
        ))}
      </dl>
      {node.vidPid && (
        <a
          href={`https://devicehunt.com/view/type/usb/vendor/${node.vidPid.slice(0, 4)}/device/${node.vidPid.slice(5)}`}
          target="_blank" rel="noreferrer"
        >
          Look up {node.vidPid} online
        </a>
      )}
    </aside>
  );
}
