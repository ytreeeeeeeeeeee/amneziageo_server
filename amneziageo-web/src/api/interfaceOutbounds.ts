import { commonDraftOf } from "./outboundContracts"
import type { OutboundBase, OutboundDraftBase } from "./outboundContracts"

export interface InterfaceOutboundDraft extends OutboundDraftBase {
  kind: "interface"
  interfaceName: string
  ipv4: boolean
  ipv6: boolean
  gateway4: string
  gateway6: string
}

export type InterfaceOutbound = OutboundBase & InterfaceOutboundDraft

export function interfaceDraftOf(outbound: InterfaceOutbound): InterfaceOutboundDraft {
  return {
    ...commonDraftOf(outbound),
    kind: "interface",
    interfaceName: outbound.interfaceName,
    ipv4: outbound.ipv4,
    ipv6: outbound.ipv6,
    gateway4: outbound.gateway4,
    gateway6: outbound.gateway6,
  }
}
