import { useCallback, useEffect, useState } from 'react';
import { emptyContact, hostContact, hostHistory, hostKey, type HostData } from './data/store';
import { loadFiles, loadHttp, refreshHttpHosts } from './data/sources';
import { TopologyView } from './components/TopologyView';
import { TimelineView } from './components/TimelineView';
import { FleetView } from './components/FleetView';
import { FindingsView } from './components/FindingsView';
import { parseEnvelope, parseEventStream } from './contract/parse';
import surfaceChain from './contract/fixtures/surface-chain.v1.json';
import kvmMini from './contract/fixtures/kvm-mini.events.jsonl?raw';
import kvmSurface from './contract/fixtures/kvm-surface.events.jsonl?raw';
import './App.css';

type Tab = 'topology' | 'findings' | 'timeline' | 'fleet';

export function App() {
  const [hosts, setHosts] = useState<HostData[]>([]);
  const [tab, setTab] = useState<Tab>('topology');
  const [activeHost, setActiveHost] = useState<string>();
  const [error, setError] = useState<string>();
  const [url, setUrl] = useState('');
  const [selfChecked, setSelfChecked] = useState(false);

  // When a collector serves this bundle itself — ConnectionDoctor.exe on
  // Windows, TBDoctor on macOS — the machine you are sitting at is already
  // behind this origin. Adopting it means opening the URL is the whole setup.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const host = await loadHttp(window.location.origin);
        if (!cancelled) setHosts((prev) => (prev.length > 0 ? prev : [host]));
      } catch {
        // Served by Vite in dev, or from a static host: no local collector.
      } finally {
        if (!cancelled) setSelfChecked(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const addUrl = async () => {
    if (!url) return;
    try {
      const host = await loadHttp(url.includes('://') ? url : `http://${url}`);
      // Replace by identity, not by name: a renamed host is the same endpoint
      // and must not appear twice, and two machines sharing a hostname must
      // not collapse into one.
      const key = hostKey(host);
      setHosts((prev) => [...prev.filter((h) => hostKey(h) !== key), host]);
      setUrl('');
      setError(undefined);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  const refresh = async () => {
    const { hosts: next, errors } = await refreshHttpHosts(hosts);
    setHosts(next);
    setError(errors.length > 0 ? errors.join(' · ') : undefined);
  };

  const active = hosts.find((h) => h.name === activeHost) ?? hosts.find((h) => h.envelope);

  const onDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault();
    try {
      const next = await loadFiles([...e.dataTransfer.files], hosts);
      setHosts(next);
      setError(undefined);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [hosts]);

  const loadFixtures = () => {
    const at = new Date().toISOString();
    setHosts([
      { name: 'm3pro', envelope: parseEnvelope(surfaceChain), events: [], origin: 'fixture', contact: { ...emptyContact(), contractAt: at }, historyReasons: [] },
      { name: 'mini', events: parseEventStream(kvmMini).events, origin: 'fixture', contact: { ...emptyContact(), eventsAt: at }, historyReasons: [] },
      { name: 'surface', events: parseEventStream(kvmSurface).events, origin: 'fixture', contact: { ...emptyContact(), eventsAt: at }, historyReasons: [] },
    ]);
    setError(undefined);
  };

  return (
    <div className="app" onDrop={onDrop} onDragOver={(e) => e.preventDefault()}>
      <header className="app-header">
        <h1>Connection Dashboard</h1>
        <nav>
          {(['topology', 'findings', 'timeline', 'fleet'] as const).map((t) => (
            <button key={t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>
              {t[0].toUpperCase() + t.slice(1)}
            </button>
          ))}
        </nav>
        <span className="spacer" />
        {hosts.length > 1 && (
          <select value={active?.name} onChange={(e) => setActiveHost(e.target.value)}>
            {hosts.map((h) => <option key={h.name}>{h.name}</option>)}
          </select>
        )}
        {active && <HostStateChips host={active} />}
        <input placeholder="collector host:port" value={url}
          onChange={(e) => setUrl(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void addUrl(); }} />
        <button onClick={() => void addUrl()}>Add host</button>
        {hosts.some((h) => h.origin.startsWith('http')) && (
          <button onClick={() => void refresh()}>Refresh</button>
        )}
        <button onClick={loadFixtures}>Load fleet fixtures</button>
      </header>

      {error && <p className="error">{error}</p>}

      {hosts.length === 0 ? (
        <div className="empty-state">
          {selfChecked && (
            <p>Drop Connection Contract v1 files anywhere — an envelope <code>.json</code> per host,
              and/or an events <code>.jsonl</code> — add a collector by address, or load the built-in
              fixtures (real recordings from an M3 Pro + Surface TB4 dock chain and a KVM switch).</p>
          )}
        </div>
      ) : (
        <>
          {tab === 'topology' && (active?.envelope ? (
            <TopologyView envelope={active.envelope}
              recordedLabel={`recorded ${active.envelope.capturedAt}`} />
          ) : <p className="empty">Selected host has no envelope loaded.</p>)}
          {tab === 'findings' && (
            <FindingsView findings={active?.envelope?.findings} analysis={active?.envelope?.analysis}
              hostName={active?.name} eventCount={active?.events.length ?? 0}
              lastEventAt={active?.events.at(-1)?.t}
              history={active ? hostHistory(active) : undefined}
              host={active} onChanged={() => void refresh()} />
          )}
          {tab === 'timeline' && (
            <TimelineView events={active?.events ?? []} snapshot={active?.envelope}
              recordedLabel={active ? `recorded · ${active.origin}` : ''}
              history={active ? hostHistory(active) : undefined}
              eventsError={active?.contact.eventsError} />
          )}
          {tab === 'fleet' && <FleetView hosts={hosts} />}
        </>
      )}
    </div>
  );
}

/** Two independent axes, two chips: contact (live / stale / offline) and
 *  history (complete / no-history / envelope-only / incomplete + reasons).
 *  Polite live regions so a refresh that changes state is announced. */
function HostStateChips({ host }: { host: HostData }) {
  const contact = hostContact(host);
  const history = hostHistory(host);
  const contactTone = contact === 'live' ? 'ok' : contact === 'stale' ? 'warn' : 'crit';
  const historyTone = history.state === 'complete' ? 'ok' : history.state === 'no-history' ? 'muted' : 'warn';
  return (
    <>
      <span className={`chip ${contactTone}`} role="status" aria-live="polite" title={host.contact.contractError ?? host.contact.eventsError ?? ''}>
        {contact}{host.contact.contractError ? ' · contract failed' : ''}
      </span>
      <span className={`chip ${historyTone}`} role="status" aria-live="polite" title={history.reasons.join('; ')}>
        history: {history.state}{history.reasons.length > 0 ? ` (${history.reasons.slice(0, 2).join(', ')}${history.reasons.length > 2 ? ', …' : ''})` : ''}
      </span>
    </>
  );
}
