import { useState } from 'react'
import { Check, Loader2, ShieldOff, Sparkles, Wrench, X } from 'lucide-react'
import { api } from '../api'
import type { ArchitectureModel, Finding, FixProposal, Project } from '../types'
import { Button, Pill, SeverityBadge } from '../ui'

export default function FindingDetail({ project, finding, aiAvailable, onClose, onProjectChanged, onFocusComponent }: {
  project: Project; finding: Finding; aiAvailable: boolean; onClose: () => void
  onProjectChanged: (p: Project, note?: string) => void; onFocusComponent: (id: string) => void
}) {
  const model: ArchitectureModel = project.model
  const existing = project.proposals?.[finding.id] ?? null
  const [proposal, setProposal] = useState<FixProposal | null>(existing)
  const [busy, setBusy] = useState<'fix' | 'apply' | 'status' | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const name = (id: string) => model.components.find(c => c.id === id)?.name ?? id
  const flowLabel = (id: string) => { const f = model.flows.find(x => x.id === id); return f ? `${name(f.from)} → ${name(f.to)}` : id }

  async function propose() {
    setBusy('fix'); setErr(null)
    try { setProposal(await api.proposeFix(project.id, finding.id)) } catch (e) { setErr((e as Error).message) } finally { setBusy(null) }
  }
  async function apply() {
    setBusy('apply'); setErr(null)
    try {
      const before = project.score
      const p = await api.applyFix(project.id, finding.id)
      onProjectChanged(p, `Fix applied. Resilience ${before.resilience} → ${p.score.resilience}, Security ${before.security} → ${p.score.security}.`)
    } catch (e) { setErr((e as Error).message) } finally { setBusy(null) }
  }
  async function setStatus(status: 'Open' | 'Accepted') {
    setBusy('status'); setErr(null)
    try { onProjectChanged(await api.setStatus(project.id, finding.id, status)) } catch (e) { setErr((e as Error).message) } finally { setBusy(null) }
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-start gap-2 border-b border-line px-4 py-3">
        <div className="min-w-0 flex-1">
          <div className="mb-1 flex flex-wrap items-center gap-2">
            <SeverityBadge severity={finding.severity} />
            <Pill>{finding.category === 'Security' ? `STRIDE · ${finding.tag}` : finding.tag}</Pill>
            <Pill tone={finding.source === 'Ai' ? 'accent' : 'muted'}>{finding.source === 'Ai' ? <><Sparkles size={11} /> AI reviewer</> : `Rule ${finding.ruleId}`}</Pill>
            <Pill tone={finding.confidence === 'High' ? 'ok' : 'warn'}>confidence {finding.confidence.toLowerCase()}</Pill>
            {finding.status !== 'Open' && <Pill tone={finding.status === 'Fixed' ? 'ok' : 'muted'}>{finding.status}</Pill>}
          </div>
          <h3 className="text-base font-semibold leading-snug">{finding.title}</h3>
        </div>
        <button onClick={onClose} className="rounded p-1 text-muted hover:bg-line hover:text-ink"><X size={16} /></button>
      </div>

      <div className="sf-scroll min-h-0 flex-1 overflow-auto px-4 py-3 text-sm">
        <p className="text-ink">{finding.description}</p>
        <Section title="Why it matters">{finding.rationale}</Section>
        <Section title="Mitigation">{finding.mitigation}</Section>

        {(finding.componentIds.length > 0 || finding.flowIds.length > 0) && (
          <Section title="Where">
            <div className="flex flex-wrap gap-1.5">
              {finding.componentIds.map(id => (
                <button key={id} onClick={() => onFocusComponent(id)} className="rounded-md border border-line bg-panel-2 px-2 py-0.5 text-xs hover:border-accent">{name(id)}</button>
              ))}
              {finding.flowIds.map(id => <span key={id} className="rounded-md border border-line px-2 py-0.5 text-xs text-muted">{flowLabel(id)}</span>)}
            </div>
          </Section>
        )}

        {finding.evidence.length > 0 && (
          <Section title="Evidence">
            <ul className="space-y-1.5">
              {finding.evidence.map((e, i) => (
                <li key={i} className="rounded-md border border-line bg-black/30 p-2 font-mono text-[11px]">
                  <div className="text-accent">{e.path ? `${e.path}${e.line ? `:${e.line}` : ''}` : e.source}</div>
                  {e.snippet && <div className="mt-0.5 whitespace-pre-wrap break-all text-muted">{e.snippet}</div>}
                </li>
              ))}
            </ul>
          </Section>
        )}

        {proposal && (
          <Section title="Proposed fix">
            <div className="rounded-lg border border-accent/40 bg-accent/5 p-3">
              <div className="font-medium">{proposal.summary}</div>
              <p className="mt-1 text-muted">{proposal.explanation}</p>
              {proposal.codeChanges.map((c, i) => (
                <div key={i} className="mt-3">
                  <div className="mb-1 text-xs text-muted">{c.description}{c.path ? <span className="ml-1 font-mono text-accent">{c.path}</span> : null}</div>
                  <pre className="sf-scroll overflow-auto rounded-md bg-black/50 p-2 font-mono text-[11px] leading-relaxed text-ink">{c.snippet}</pre>
                </div>
              ))}
              <div className="mt-3 text-xs text-muted">
                Model changes: {proposal.patch.ops.length === 0 ? 'none' : proposal.patch.ops.map(o => `${o.kind}${o.id ? ` ${o.id}` : ''}${o.property ? `.${o.property}=${JSON.stringify(o.value)}` : ''}`).join('; ')}
              </div>
              {proposal.resolvesFindingIds.length > 1 && <div className="mt-1 text-xs text-ok">Also resolves {proposal.resolvesFindingIds.length - 1} related finding(s).</div>}
              {proposal.residualRisk && <div className="mt-1 text-xs text-muted">Residual risk: {proposal.residualRisk}</div>}
            </div>
          </Section>
        )}

        {err && <div className="mt-3 rounded-md border border-critical/40 bg-critical/10 p-2 text-xs text-critical">{err}</div>}
      </div>

      <div className="flex flex-wrap items-center gap-2 border-t border-line px-4 py-3">
        {finding.status === 'Open' && !proposal && (
          <Button onClick={propose} disabled={!aiAvailable || busy !== null} title={aiAvailable ? '' : 'Needs an AI provider'}>
            {busy === 'fix' ? <Loader2 size={14} className="animate-spin" /> : <Wrench size={14} />} Propose fix with AI
          </Button>
        )}
        {finding.status === 'Open' && proposal && (
          <>
            <Button onClick={apply} disabled={busy !== null || proposal.patch.ops.length === 0} title={proposal.patch.ops.length === 0 ? 'This proposal has no model changes to apply' : ''}>
              {busy === 'apply' ? <Loader2 size={14} className="animate-spin" /> : <Check size={14} />} Apply fix &amp; re-score
            </Button>
            <Button variant="ghost" onClick={propose} disabled={busy !== null}>Regenerate</Button>
          </>
        )}
        {finding.status === 'Open' && (
          <Button variant="ghost" onClick={() => setStatus('Accepted')} disabled={busy !== null} className="ml-auto"><ShieldOff size={14} /> Accept risk</Button>
        )}
        {finding.status === 'Accepted' && <Button variant="ghost" onClick={() => setStatus('Open')} disabled={busy !== null}>Reopen</Button>}
      </div>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mt-4">
      <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-muted">{title}</div>
      <div className="text-ink">{children}</div>
    </div>
  )
}
