import type { ContractEnvelope, ContractEvent } from '../contract/types';

export interface HostData {
  name: string;
  envelope?: ContractEnvelope;
  events: ContractEvent[];
  /** Where this came from — file names or a URL; recorded data is labelled. */
  origin: string;
}
