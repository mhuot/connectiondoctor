import { useCallback, useState } from 'react';
import type { HostData } from './data/store';
import { loadFiles, loadHttp, refreshHttpHosts } from './data/sources';
import { TopologyView } from './components/TopologyView';
import { TimelineView } from './components/TimelineView';
import { FleetView } from './components/FleetView';
import { parseEnvelope, parseEventStream } from './contract/parse';
import surfaceChain from './contract/fixtures/surface-chain.v1.json';
import kvmMini from './contract/fixtures/kvm-mini.events.jsonl?raw';
import kvmSurface from './contract/fixtures/kvm-surface.events.jsonl?raw';
import './App.css';

type Tab = 'topology' | 'timeline' | 'fleet';

export function App() {
  const [hosts, setHosts] = useState<HostData[]>([]);
  const [tab, setTab] = useState<Tab>('topology');
  const [activeHost, setActiveHost] = useState<string>();
  const [error, setError] = useState<string>();
  const [url, setUrl] = useState('');

  const addUrl = async () => {
    if (!url) return;
    try {
      const host = await loadHttp(url.includes('://') ? url : `http://${url}`);
      setHosts((prev) => [...prev.filter((h) => h.name !== host.name), host]);
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
    setHosts([
      { name: 'm3pro', envelope: parseEnvelope(surfaceChain), events: [], origin: 'fixture' },
      { name: 'mini', events: parseEventStream(kvmMini).events, origin: 'fixture' },
      { name: 'surface', events: parseEventStream(kvmSurface).events, origin: 'fixture' },
    ]);
    setError(undefined);
  };

  return (
    <div className="app" onDrop={onDrop} onDragOver={(e) => e.preventDefault()}>
      <header className="app-header">
        <h1>Connection Dashboard</h1>
        <nav>
          {(['topology', 'timeline', 'fleet'] as const).map((t) => (
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
          <p>Drop Connection Contract v1 files anywhere — an envelope <code>.json</code> per host,
            and/or an events <code>.jsonl</code> — or load the built-in fixtures
            (real recordings from an M3 Pro + Surface TB4 dock chain and a KVM switch).</p>
        </div>
      ) : (
        <>
          {tab === 'topology' && (active?.envelope ? (
            <TopologyView envelope={active.envelope}
              recordedLabel={`recorded ${active.envelope.capturedAt}`} />
          ) : <p className="empty">Selected host has no envelope loaded.</p>)}
          {tab === 'timeline' && (
            <TimelineView events={active?.events ?? []} snapshot={active?.envelope}
              recordedLabel={active ? `recorded · ${active.origin}` : ''} />
          )}
          {tab === 'fleet' && <FleetView hosts={hosts} />}
        </>
      )}
    </div>
  );
}
