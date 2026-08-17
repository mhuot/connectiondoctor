import type { LinkProtocol, NodeKind } from '../contract/types';

/** The colours the topology, its legend and the inspector all read from.
 *
 *  They live outside the component that draws with them for two reasons. The
 *  mechanical one is React Fast Refresh: a file that exports anything other
 *  than components loses hot reload for the components in it. The one that
 *  will matter longer is that a protocol's colour is a fact about the contract
 *  vocabulary, not about one view — the same yellow has to mean power in the
 *  diagram, the legend and anything drawn later, and a colour defined beside a
 *  single component drifts the moment a second one needs it.
 *
 *  Keyed by the contract's own enums, so a new protocol or node kind is a type
 *  error here rather than a silently uncoloured node on screen. */
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
