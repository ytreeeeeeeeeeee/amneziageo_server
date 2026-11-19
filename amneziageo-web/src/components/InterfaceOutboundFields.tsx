import type { InterfaceOutboundDraft } from "@/api/interfaceOutbounds"
import { Flag, Line, ListLine, Part } from "@/components/fields"
import { useText } from "@/i18n"

export function InterfaceOutboundFields({ draft, setDraft }: {
  draft: InterfaceOutboundDraft
  setDraft: (draft: InterfaceOutboundDraft) => void
}) {
  const t = useText()
  function put(change: Partial<InterfaceOutboundDraft>) {
    setDraft({ ...draft, ...change })
  }
  return <Part title={t("outbounds.kindInterface")}>
    <Line id="outbound-interface" caption={t("outbounds.interfaceName")}
      value={draft.interfaceName} onChange={(interfaceName) => put({ interfaceName })} wide />
    <Flag id="outbound-ipv4" caption={t("outbounds.ipv4")} value={draft.ipv4}
      onChange={(ipv4) => put({ ipv4, gateway4: ipv4 ? draft.gateway4 : "" })} />
    <Flag id="outbound-ipv6" caption={t("outbounds.ipv6")} value={draft.ipv6}
      onChange={(ipv6) => put({ ipv6, gateway6: ipv6 ? draft.gateway6 : "" })} />
    {!draft.ipv4 && !draft.ipv6 && <div role="alert" className="text-sm text-alarm sm:col-span-2">
      {t("error.badIpFamilies")}
    </div>}
    {draft.ipv4 && <Line id="outbound-gateway4" caption={t("outbounds.gateway4")}
      value={draft.gateway4} onChange={(gateway4) => put({ gateway4 })} />}
    {draft.ipv6 && <Line id="outbound-gateway6" caption={t("outbounds.gateway6")}
      value={draft.gateway6} onChange={(gateway6) => put({ gateway6 })} />}
    <div className="text-sm text-muted sm:col-span-2">{t("outbounds.gatewayHint")}</div>
    <ListLine id="outbound-dns" caption={t("outbounds.dns")} value={draft.dns}
      onChange={(dns) => put({ dns })} wide />
    <Flag id="outbound-private" caption={t("outbounds.closePrivate")} value={draft.closePrivate}
      onChange={(closePrivate) => put({ closePrivate })} />
  </Part>
}
