export interface OutboundProbe {
  isReached: boolean
  falls: number
  at: string
}

export interface OutboundState {
  hasLink: boolean
  rxBytes: number | null
  txBytes: number | null
  isAlive: boolean
  fault: string
  carries: boolean
  probe: OutboundProbe | null
}

export interface OutboundDraftBase {
  name: string
  kind: string
  isEnabled: boolean
  dns: string[]
  probe: string
  probeEvery: number
  closePrivate: boolean
  balancers?: number[]
  rules?: number[]
}

export interface OutboundBase extends OutboundDraftBase {
  id: number
  position: number
  mark: number
  table: number
  state: OutboundState | null
  createdUtc: string
  updatedUtc: string
}

export function commonDraftOf(outbound: OutboundBase): Omit<OutboundDraftBase, "kind"> {
  return {
    name: outbound.name,
    isEnabled: outbound.isEnabled,
    dns: outbound.dns,
    probe: outbound.probe,
    probeEvery: outbound.probeEvery,
    closePrivate: outbound.closePrivate,
  }
}
