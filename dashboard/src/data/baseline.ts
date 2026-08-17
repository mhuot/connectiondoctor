import type { HostData } from './store';

/** The dashboard half of docs/embedding.md § Mutations: same-origin only, the
 *  custom header that forces a preflight, and If-Match carrying the capture
 *  time the user was actually shown so a second tab cannot clobber a newer
 *  baseline. Errors come back as the server's structured JSON. */
export interface BaselineResult {
  ok: boolean;
  capturedAt?: string;
  replaced?: boolean;
  error?: string;
  current?: { capturedAt: string };
}

/** True when this host is the collector serving the page: mutations are only
 *  offered there (loopback, same origin). */
export function canMutate(host: HostData): boolean {
  if (typeof window === 'undefined') return false;
  return host.origin === window.location.origin;
}

/** The exact header value the server accepts: the capture time, quoted. */
export function ifMatchHeader(capturedAt: string): string {
  return `"${capturedAt}"`;
}

export async function captureBaseline(host: HostData, options: { replace?: boolean; ifMatch?: string } = {}): Promise<BaselineResult> {
  const url = new URL('/baseline', host.origin);
  if (options.replace) url.searchParams.set('replace', '1');
  const headers: Record<string, string> = { 'X-ConnectionDoctor-Request': '1' };
  // Exactly one strong ETag, quoted — the server refuses anything else
  // (docs/embedding.md § Mutations), and an unquoted timestamp would make
  // every replacement fail.
  if (options.ifMatch) headers['If-Match'] = ifMatchHeader(options.ifMatch);

  let response: Response;
  try {
    response = await fetch(url, { method: 'POST', headers });
  } catch (cause) {
    return { ok: false, error: cause instanceof Error ? cause.message : String(cause) };
  }

  const body = (await response.json().catch(() => ({}))) as Record<string, unknown>;
  if (response.ok) {
    const baseline = body.baseline as { capturedAt?: string } | undefined;
    return { ok: true, capturedAt: baseline?.capturedAt, replaced: Boolean(body.replaced) };
  }
  return {
    ok: false,
    error: typeof body.error === 'string' ? body.error : `HTTP ${response.status}`,
    current: body.current as { capturedAt: string } | undefined,
  };
}
