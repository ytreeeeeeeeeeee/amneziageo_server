import { Navigate, useNavigate, useParams } from "react-router-dom"
import { complaint } from "@/api/auth"
import {
  draftOf,
  outboundSaved,
  useAddOutbound,
  useApplyOutbound,
  useChangeOutbound,
  useFreshOutbound,
  useOutbounds,
  useProbeOutbound,
} from "@/api/outbounds"
import type { Outbound } from "@/api/outbounds"
import { OutboundForm } from "@/components/OutboundForm"
import { HeadActions } from "@/components/Section"
import { secondary } from "@/components/styles"
import { useTail } from "@/components/crumbs"
import { useText } from "@/i18n"
import { useSpot } from "@/store/spots"

export function OutboundPage() {
  const { outboundId } = useParams()

  return outboundId === undefined ? <NewOutbound /> : <HeldOutbound outboundId={Number(outboundId)} />
}

function NewOutbound() {
  const t = useText()
  const navigate = useNavigate()
  const back = useSpot("/routing/channels")
  const outbounds = useOutbounds()
  const fresh = useFreshOutbound(outbounds.data !== undefined, nextName(outbounds.data), "wg")
  const add = useAddOutbound()

  useTail([{ label: t("outbounds.newTitle") }])

  if (fresh.data === undefined) {
    return <div className="mt-4 text-sm text-muted">{t("outbounds.loading")}</div>
  }

  return (
    <OutboundForm
      start={draftOf(fresh.data)}
      source={fresh.data}
      pending={add.isPending}
      error={add.error}
      onSave={(draft) => add.mutate(draft, {
        onSuccess: () => navigate(back),
        onError: (error) => {
          if (outboundSaved(error)) {
            navigate(back, { state: { outboundPending: true } })
          }
        },
      })}
      onClose={() => navigate(back)}
    />
  )
}

function HeldOutbound({ outboundId }: { outboundId: number }) {
  const t = useText()
  const navigate = useNavigate()
  const back = useSpot("/routing/channels")
  const outbounds = useOutbounds()
  const change = useChangeOutbound()
  const apply = useApplyOutbound()
  const probe = useProbeOutbound()
  const applyError = apply.error === null ? null : complaint(apply.error)
  const all = outbounds.data ?? []
  const held = all.find((one) => one.id === outboundId)

  useTail(held === undefined ? [] : [{ label: held.name }])

  if (held === undefined) {
    return outbounds.data === undefined ? (
      <div className="mt-4 text-sm text-muted">{t("outbounds.loading")}</div>
    ) : (
      <Navigate to={back} replace />
    )
  }

  return (
    <div>
      <HeadActions>
        <button
          type="button"
          onClick={() => probe.mutate(held.id)}
          disabled={probe.isPending}
          className={`flex h-10 items-center ${secondary}`}
        >
          {t("outbounds.probeNow")}
        </button>
        <button
          type="button"
          onClick={() => apply.mutate(held.id)}
          disabled={apply.isPending}
          className={`flex h-10 items-center ${secondary}`}
        >
          {t("outbounds.apply")}
        </button>
      </HeadActions>

      {applyError !== null && (
        <div role="alert" className="mb-3 text-sm text-alarm">
          {t(applyError === "error.hostRefused" ? "outbounds.applyPending" : applyError)}
        </div>
      )}

      <OutboundForm
        start={draftOf(held)}
        source={held}
        self={held.name}
        pending={change.isPending}
        error={change.error}
        onSave={(draft) => change.mutate({ id: held.id, draft }, {
          onSuccess: () => navigate(back),
          onError: (error) => {
            if (outboundSaved(error)) {
              navigate(back, { state: { outboundPending: true } })
            }
          },
        })}
        onClose={() => navigate(back)}
        onRemove={() => navigate(`/routing/channels/${held.id}/delete`)}
      />
    </div>
  )
}

function nextName(outbounds: Outbound[] | undefined): string {
  const taken = new Set((outbounds ?? []).map((one) => one.name))
  let at = 1
  while (taken.has(`out${at}`)) {
    at++
  }

  return `out${at}`
}
