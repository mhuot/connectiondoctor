import type { ContractEnvelope, ContractEvent } from '../contract/types';

/** When each half of a host last arrived, and how the last attempt went.
 *  Kept per half because the two axes fail independently: a collector can
 *  answer /contract and not /events, and a stale envelope must never be
 *  paired silently with fresh events. */
export interface HostContact {
  /** ISO time of the last successful /contract (or file load). */
  contractAt?: string;
  /** ISO time of the last successful /events (or file load). */
  eventsAt?: string;
  contractError?: string;
  eventsError?: string;
  /** Lines the parser skipped in the last successful events fetch. Durable:
   *  a later success cannot un-skip them (see hostHistory). */
  skippedLines: number;
}

export interface HostData {
  name: string;
  envelope?: ContractEnvelope;
  events: ContractEvent[];
  /** Where this came from — file names or a URL; recorded data is labelled. */
  origin: string;
  contact: HostContact;
  /** Durable history-quality reasons for this host; cleared only when a later
   *  payload proves the window complete (see hostHistory). */
  historyReasons: string[];
}

export const emptyContact = (): HostContact => ({ skippedLines: 0 });

export type ContactState = 'live' | 'stale' | 'offline';
export type HistoryState = 'complete' | 'no-history' | 'envelope-only' | 'incomplete';

/** Live within twice the refresh interval; stale after; offline when the
 *  last refresh failed for both halves. */
export const LIVE_WINDOW_MS = 2 * 30_000;

export function hostContact(host: HostData, now = Date.now()): ContactState {
  const { contractAt, eventsAt, contractError, eventsError } = host.contact;
  if (!host.origin.startsWith('http')) return 'live'; // files do not go stale
  const last = Math.max(contractAt ? Date.parse(contractAt) : 0, eventsAt ? Date.parse(eventsAt) : 0);
  if (last === 0) return 'offline';
  if (contractError && eventsError) return 'offline';
  return now - last <= LIVE_WINDOW_MS ? 'live' : 'stale';
}

/** History quality is decided by producer coverage (authoritative), the events
 *  fetch, and the durable reasons — never inferred from the first event. */
export function hostHistory(host: HostData): { state: HistoryState; reasons: string[] } {
  const analysis = host.envelope?.analysis;
  const reasons = [...host.historyReasons];
  if (host.contact.eventsError) reasons.push(`events-fetch-failed: ${host.contact.eventsError}`);
  if (host.contact.skippedLines > 0) reasons.push(`${host.contact.skippedLines} skipped lines`);
  if (analysis && !analysis.coverage.complete) reasons.push(...(analysis.coverage.reasons ?? ['coverage-incomplete']));

  // "Never recorded" is only claimable when there is nothing to explain: a
  // stream whose every line was corrupt has zero events *and* a reason, and
  // must not look like a machine that has never run the recorder.
  if (!analysis && host.events.length === 0 && !host.contact.eventsError && reasons.length === 0) {
    return { state: 'no-history', reasons: [] };
  }
  if (host.contact.eventsError && host.events.length === 0) {
    return { state: 'envelope-only', reasons: dedupe(reasons) };
  }
  if (reasons.length > 0) return { state: 'incomplete', reasons: dedupe(reasons) };
  if (analysis?.coverage.complete) return { state: 'complete', reasons: [] };
  // Events present, no producer coverage to vouch for the window.
  return { state: 'incomplete', reasons: ['coverage-unknown'] };
}

/** Merge a fresh fetch into a host, keeping the two axes honest:
 *  - a failed /events keeps the previous events (marked stale via eventsError),
 *    never an empty stream that reads as "no incidents";
 *  - durable history reasons persist until a payload proves the requested
 *    window complete with zero skipped lines. */
export function mergeRefresh(previous: HostData | undefined, fresh: HostData): HostData {
  const prevReasons = previous?.historyReasons ?? [];
  const provesComplete =
    !fresh.contact.eventsError &&
    fresh.contact.skippedLines === 0 &&
    fresh.envelope?.analysis?.coverage.complete === true;
  const carried = provesComplete ? [] : prevReasons;
  const durable = new Set(carried);
  if (fresh.contact.skippedLines > 0) durable.add(`${fresh.contact.skippedLines} skipped lines`);
  if (fresh.contact.eventsError) durable.add('events-fetch-failed');
  const events = fresh.contact.eventsError && previous ? previous.events : fresh.events;
  const eventsAt = fresh.contact.eventsError && previous ? previous.contact.eventsAt : fresh.contact.eventsAt;
  return {
    ...fresh,
    events,
    contact: { ...fresh.contact, eventsAt },
    historyReasons: [...durable],
  };
}

const dedupe = (xs: string[]): string[] => [...new Set(xs)];
