import { commonDraftOf } from "./outboundContracts"
import type { OutboundBase, OutboundDraftBase } from "./outboundContracts"

export interface LocalOutbound extends OutboundBase {
  kind: "local"
}

export interface LocalOutboundDraft extends OutboundDraftBase {
  kind: "local"
}

export function localDraftOf(outbound: LocalOutbound): LocalOutboundDraft {
  return {
    ...commonDraftOf(outbound),
    kind: "local",
  }
}
