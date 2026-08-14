import { useMemo } from 'react';
import type { HostData } from '../data/store';
import { detectMigrations } from '../domain/migrate';

/** Multiple hosts side by side, with cross-host migrations rendered as single
 *  branch moves — the KVM case: hub + keyboard + mouse is one arrow, not four. */
export function FleetView({ hosts }: { hosts: HostData[] }) {
  const hubVidPids = useMemo(
    () =>
      new Set(
        hosts.flatMap((h) => h.envelope?.nodes.filter((n) => n.usbClass === 9 && n.vidPid).map((n) => n.vidPid!) ?? []),
      ),
    [hosts],
  );
  const result = useMemo(
    () =>
      detectMigrations(
        hosts.filter((h) => h.events.length > 0).map((h) => ({ host: h.name, events: h.events })),
        { hubVidPids },
      ),
    [hosts, hubVidPids],
  );

  if (hosts.length === 0) {
    return <p className="empty">Drop contract files to populate the fleet — one envelope and/or events stream per host.</p>;
  }

  return (
    <div className="fleet">
      <div className="cards">
        {hosts.map((h) => (
          <div className="card" key={h.name}>
            <h3>{h.name}</h3>
            {h.envelope ? (
              <>
                <p>{h.envelope.host.model ?? h.envelope.host.os} · {h.envelope.power.source}
                  {h.envelope.power.adapter?.watts ? ` ${h.envelope.power.adapter.watts}W` : ''}</p>
                <p>{h.envelope.nodes.length} nodes · recorded {h.envelope.capturedAt}</p>
              </>
            ) : (
              <p className="empty">events only</p>
            )}
            <p>{h.events.length} events</p>
          </div>
        ))}
      </div>

      <h3>Migrations</h3>
      {result.migrations.length === 0 && <p className="empty">No cross-host migrations in the loaded windows.</p>}
      {result.migrations.map((m) => (
        <div className="migration" key={`${m.from}-${m.to}-${m.at}`}>
          <strong>{m.from} → {m.to}</strong> at {m.at}
          {m.branchRoot && <> — branch <code>{m.branchRoot.name}</code> ({m.branchRoot.vidPid})</>}
          <div className="devices">{m.devices.map((d) => d.name).join(', ')}</div>
        </div>
      ))}
      {(result.unmatchedRemovals.length > 0 || result.unmatchedAdds.length > 0) && (
        <p className="muted">
          {result.unmatchedRemovals.length} removal(s) and {result.unmatchedAdds.length} add(s) did not
          match a migration — genuinely gone or genuinely new.
        </p>
      )}
    </div>
  );
}
