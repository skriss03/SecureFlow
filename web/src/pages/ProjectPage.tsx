import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { ArrowLeft, Bomb, Loader2, RefreshCw, Sparkles, X } from 'lucide-react'
import { api, subscribe } from '../api'
import type { BlastRadiusResult, Component, Finding, LogEntry, Project, ProjectStatus } from '../types'
import { Button, Card, Pill, SeverityBadge, TypeIcon, impactColor, severityRank } from '../ui'
import ArchitectureGraph from '../components/ArchitectureGraph'
import FindingsPanel from '../components/FindingsPanel'
import FindingDetail from '../components/FindingDetail'
import ProgressLog from '../components/ProgressLog'
import ReportView from '../components/ReportView'
import ScoreGauge from '../components/ScoreGauge'

type Tab = 'model' | 'findings' | 'report'

export default function ProjectPage({ aiAvailable }: { aiAvailable: boolean }) {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const [project, setProject] = useState<Project | null>(null)
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [status, setStatus] = useState<ProjectStatus>('Queued')
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('model')
  const [selectedFinding, setSelectedFinding] = useState<Finding | null>(null)
  const [selectedComponent, setSelectedComponent] = useState<string | null>(null)
  const [blast, setBlast] = useState<BlastRadiusResult | null>(null)
  const [toast, setToast] = useState<string | null>(null)
  const [reanalyzing, setReanalyzing] = useState(false)
  const [reportVersion, setReportVersion] = useState(0)

  const load = useCallback(() => api.project(id).then(p => { setProject(p); setStatus(p.status); setError(p.error ?? null); setLogs(p.log) }), [id])

  useEffect(() => {
    let dispose: (() => void) | null = null
    load().then(() => {
      dispose = subscribe(id, e => setLogs(l => [...l, e]), (s, err) => {
        setStatus(s); if (err) setError(err)
        if (s === 'Ready' || s === 'Error') { load(); dispose?.(); }
      })
    }).catch(e => setError((e as Error).message))
    return () => dispose?.()
  }, [id, load])

  useEffect(() => { if (!toast) return; const t = setTimeout(() => setToast(null), 5000); return () => clearTimeout(t) }, [toast])

  const onProjectChanged = (p: Project, note?: string) => {
    setProject(p); setReportVersion(v => v + 1); setBlast(null)
    if (selectedFinding) setSelectedFinding(p.findings.find(f => f.id === selectedFinding.id) ?? null)
    if (note) setToast(note)
  }

  const reanalyze = async () => {
    setReanalyzing(true)
    try { onProjectChanged(await api.reanalyze(id), 'Re-analysis complete.') } catch (e) { setToast((e as Error).message) } finally { setReanalyzing(false) }
  }

  const simulate = async (componentId: string) => {
    try { setBlast(await api.simulate(id, componentId)); setSelectedFinding(null) } catch (e) { setToast((e as Error).message) }
  }

  const focusComponent = (cid: string) => { setTab('model'); setSelectedComponent(cid); setBlast(null) }
  const selectFinding = (f: Finding) => { setSelectedFinding(f); setBlast(null) }

  const first = project?.history?.[0]
  const delta = project && first ? { r: project.score.resilience - first.resilience, s: project.score.security - first.security } : undefined

  const componentFindings = useMemo(() => {
    if (!project || !selectedComponent) return []
    return project.findings.filter(f => f.status === 'Open' && f.componentIds.includes(selectedComponent)).sort((a, b) => severityRank[b.severity] - severityRank[a.severity])
  }, [project, selectedComponent])

  if (!project) return <div className="p-8 text-muted">{error ? <span className="text-critical">{error}</span> : <span className="flex items-center gap-2"><Loader2 className="animate-spin" size={16} /> Loading…</span>}</div>

  const running = status !== 'Ready' && status !== 'Error'
  if (running || (status === 'Error' && project.model.components.length === 0)) {
    return (
      <div className="h-full">
        <div className="flex items-center gap-3 border-b border-line px-5 py-3">
          <button onClick={() => nav('/')} className="rounded p-1 text-muted hover:bg-line"><ArrowLeft size={16} /></button>
          <div className="font-semibold">{project.name}</div>
          <Pill tone={status === 'Error' ? 'danger' : 'accent'}>{status}</Pill>
        </div>
        <ProgressLog entries={logs} status={status} error={error} />
      </div>
    )
  }

  const comp: Component | undefined = selectedComponent ? project.model.components.find(c => c.id === selectedComponent) : undefined

  return (
    <div className="flex h-full flex-col">
      <div className="no-print flex flex-wrap items-center gap-4 border-b border-line bg-panel px-5 py-3">
        <button onClick={() => nav('/')} className="rounded p-1 text-muted hover:bg-line"><ArrowLeft size={16} /></button>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <h1 className="truncate text-lg font-semibold">{project.name}</h1>
            <Pill>{project.source}{project.sourceRef ? ` · ${project.sourceRef}` : ''}</Pill>
            {project.aiProvider && project.aiProvider !== 'none' && <Pill tone="accent"><Sparkles size={11} /> {project.aiProvider}</Pill>}
          </div>
          <div className="text-xs text-muted">{project.model.components.length} components · {project.model.flows.length} flows · {project.model.trustBoundaries.length} boundaries · {project.findings.filter(f => f.status === 'Open').length} open findings</div>
        </div>
        <div className="ml-auto flex items-center gap-6">
          <ScoreGauge label="Resilience" value={project.score.resilience} grade={project.score.resilienceGrade} delta={delta?.r} size={64} />
          <ScoreGauge label="Security" value={project.score.security} grade={project.score.securityGrade} delta={delta?.s} size={64} />
          <Button variant="ghost" onClick={reanalyze} disabled={reanalyzing} title="Re-run rules and the AI review">
            {reanalyzing ? <Loader2 size={14} className="animate-spin" /> : <RefreshCw size={14} />} Re-analyze
          </Button>
        </div>
      </div>

      <div className="no-print flex items-center gap-1 border-b border-line px-5">
        {(['model', 'findings', 'report'] as Tab[]).map(t => (
          <button key={t} onClick={() => setTab(t)}
            className={`border-b-2 px-3 py-2 text-sm font-medium capitalize ${tab === t ? 'border-accent text-ink' : 'border-transparent text-muted hover:text-ink'}`}>
            {t === 'model' ? 'Model & blast radius' : t}
          </button>
        ))}
        {status === 'Error' && <span className="ml-4 text-xs text-critical">Last run failed: {error}</span>}
      </div>

      <div className="relative min-h-0 flex-1">
        {toast && (
          <div className="absolute left-1/2 top-3 z-20 -translate-x-1/2 rounded-lg border border-accent/40 bg-panel px-4 py-2 text-sm shadow-lg">{toast}</div>
        )}

        {tab === 'model' && (
          <div className="flex h-full">
            <div className="min-w-0 flex-1">
              <ArchitectureGraph model={project.model} heat={project.score.componentHeat} findings={project.findings}
                selectedFinding={selectedFinding} blast={blast} selectedComponentId={selectedComponent}
                onSelectComponent={cid => { setSelectedComponent(cid); if (cid) setSelectedFinding(null) }} />
            </div>
            <aside className="sf-scroll w-[360px] shrink-0 overflow-auto border-l border-line bg-panel">
              {blast ? (
                <BlastPanel blast={blast} onClose={() => setBlast(null)} />
              ) : comp ? (
                <ComponentPanel comp={comp} findings={componentFindings} onSimulate={() => simulate(comp.id)} onSelectFinding={f => { selectFinding(f); setTab('findings') }} onClose={() => setSelectedComponent(null)} />
              ) : (
                <OverviewPanel project={project} onFocus={ids => { setSelectedComponent(ids[0] ?? null) }} onSimulate={simulate} />
              )}
            </aside>
          </div>
        )}

        {tab === 'findings' && (
          <div className="flex h-full">
            <div className={`min-w-0 border-r border-line ${selectedFinding ? 'w-[46%]' : 'flex-1'}`}>
              <FindingsPanel findings={project.findings} model={project.model} selectedId={selectedFinding?.id ?? null} onSelect={selectFinding} />
            </div>
            {selectedFinding && (
              <div className="min-w-0 flex-1 bg-panel">
                <FindingDetail key={selectedFinding.id + project.updatedAt} project={project} finding={selectedFinding} aiAvailable={aiAvailable}
                  onClose={() => setSelectedFinding(null)} onProjectChanged={onProjectChanged} onFocusComponent={focusComponent} />
              </div>
            )}
          </div>
        )}

        {tab === 'report' && <ReportView projectId={project.id} version={reportVersion} />}
      </div>
    </div>
  )
}

function OverviewPanel({ project, onFocus, onSimulate }: { project: Project; onFocus: (ids: string[]) => void; onSimulate: (id: string) => void }) {
  const a = project.analysis
  const hottest = Object.entries(project.score.componentHeat).sort((x, y) => y[1] - x[1]).slice(0, 4)
  const name = (id: string) => project.model.components.find(c => c.id === id)?.name ?? id
  return (
    <div className="p-4 text-sm">
      {a ? (
        <>
          <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-muted">Executive summary</div>
          <p className="leading-relaxed text-ink">{a.executiveSummary}</p>
          <div className="mt-3 rounded-lg border border-high/40 bg-high/10 p-3 text-xs"><span className="font-semibold text-high">Most likely bad day. </span>{a.businessImpact}</div>
          <div className="mb-1 mt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Top risks</div>
          <ol className="space-y-2">
            {a.topRisks.map((r, i) => (
              <li key={i} className="cursor-pointer rounded-lg border border-line p-2.5 hover:border-accent" onClick={() => onFocus(r.componentIds)}>
                <div className="font-medium">{i + 1}. {r.title}</div>
                <div className="mt-0.5 text-xs text-muted">{r.why}</div>
              </li>
            ))}
          </ol>
          {a.quickWins.length > 0 && (
            <>
              <div className="mb-1 mt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Quick wins</div>
              <ul className="list-disc space-y-1 pl-4 text-xs text-ink">{a.quickWins.map((q, i) => <li key={i}>{q}</li>)}</ul>
            </>
          )}
        </>
      ) : (
        <div className="rounded-lg border border-line p-3 text-xs text-muted">
          {project.aiProvider === 'none' || !project.aiProvider ? 'No AI provider configured: showing deterministic rule results only. Set ANTHROPIC_API_KEY or OPENAI_API_KEY for the executive summary, top risks and fixes.' : 'AI review was not available for this run. Use Re-analyze to try again.'}
        </div>
      )}

      <div className="mb-1 mt-5 text-[11px] font-semibold uppercase tracking-wide text-muted">Riskiest components · click to simulate failure</div>
      <div className="space-y-1.5">
        {hottest.map(([cid, h]) => (
          <button key={cid} onClick={() => onSimulate(cid)} className="flex w-full items-center gap-2 rounded-lg border border-line px-2.5 py-1.5 text-left hover:border-down">
            <Bomb size={14} className="text-down" />
            <span className="flex-1 truncate">{name(cid)}</span>
            <span className="h-1.5 w-20 overflow-hidden rounded bg-line"><span className="block h-full bg-gradient-to-r from-medium to-critical" style={{ width: `${Math.round(h * 100)}%` }} /></span>
          </button>
        ))}
      </div>

      {project.model.assumptions.length > 0 && (
        <>
          <div className="mb-1 mt-5 text-[11px] font-semibold uppercase tracking-wide text-muted">Extractor assumptions</div>
          <ul className="list-disc space-y-1 pl-4 text-xs text-muted">{project.model.assumptions.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </>
      )}
    </div>
  )
}

function ComponentPanel({ comp, findings, onSimulate, onSelectFinding, onClose }: {
  comp: Component; findings: Finding[]; onSimulate: () => void; onSelectFinding: (f: Finding) => void; onClose: () => void
}) {
  const p = comp.props
  const rows: [string, unknown][] = [
    ['Replicas', p.replicas], ['Zones', p.zones], ['Regions', p.regions], ['Backup', p.hasBackup], ['Health check', p.hasHealthCheck],
    ['Autoscale', p.autoscale], ['Encrypted at rest', p.encryptionAtRest], ['Public', p.publiclyExposed], ['Managed', p.managed],
    ['Rate limited', p.rateLimited], ['Audit logging', p.auditLogging], ['Input validation', p.inputValidation], ['Hardcoded secrets', p.hardcodedSecrets],
  ]
  const fmt = (v: unknown) => v === null || v === undefined ? <span className="text-muted">unknown</span> : v === true ? <span className="text-ok">yes</span> : v === false ? <span className="text-high">no</span> : String(v)
  const canSimulate = comp.type !== 'User' && comp.type !== 'External'
  return (
    <div className="p-4 text-sm">
      <div className="flex items-start gap-2">
        <span className="grid h-9 w-9 place-items-center rounded-lg bg-panel-2 text-accent"><TypeIcon type={comp.type} size={20} /></span>
        <div className="min-w-0 flex-1">
          <div className="truncate font-semibold">{comp.name}</div>
          <div className="text-xs text-muted">{comp.type}{comp.tier ? ` · ${comp.tier}` : ''}{p.technology ? ` · ${p.technology}` : ''}</div>
        </div>
        <button onClick={onClose} className="rounded p-1 text-muted hover:bg-line"><X size={16} /></button>
      </div>
      {comp.description && <p className="mt-2 text-xs text-muted">{comp.description}</p>}
      {canSimulate && <Button className="mt-3 w-full justify-center" variant="danger" onClick={onSimulate}><Bomb size={14} /> Simulate failure of {comp.name}</Button>}

      <div className="mb-1 mt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Properties</div>
      <table className="w-full text-xs"><tbody>
        {rows.map(([k, v]) => <tr key={k} className="border-t border-line"><td className="py-1 text-muted">{k}</td><td className="py-1 text-right">{fmt(v)}</td></tr>)}
      </tbody></table>

      {comp.evidence.length > 0 && (
        <>
          <div className="mb-1 mt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Evidence</div>
          {comp.evidence.map((e, i) => (
            <div key={i} className="mb-1 rounded-md border border-line bg-black/30 p-2 font-mono text-[11px]">
              <div className="text-accent">{e.path ? `${e.path}${e.line ? `:${e.line}` : ''}` : e.source}</div>
              {e.snippet && <div className="whitespace-pre-wrap break-all text-muted">{e.snippet}</div>}
            </div>
          ))}
        </>
      )}

      <div className="mb-1 mt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Open findings ({findings.length})</div>
      <ul className="space-y-1">
        {findings.map(f => (
          <li key={f.id} onClick={() => onSelectFinding(f)} className="flex cursor-pointer items-center gap-2 rounded-md border border-line px-2 py-1.5 hover:border-accent">
            <SeverityBadge severity={f.severity} small /><span className="truncate text-xs">{f.title}</span>
          </li>
        ))}
        {findings.length === 0 && <li className="text-xs text-muted">None. Nice.</li>}
      </ul>
    </div>
  )
}

function BlastPanel({ blast, onClose }: { blast: BlastRadiusResult; onClose: () => void }) {
  const groups = (['Killed', 'Down', 'Degraded', 'Ok'] as const).map(l => [l, blast.impacts.filter(i => i.level === l)] as const)
  return (
    <div className="p-4 text-sm">
      <div className="flex items-start gap-2">
        <Bomb size={20} className="mt-0.5 shrink-0 text-down" />
        <div className="flex-1">
          <div className="font-semibold">Blast radius</div>
          <p className="mt-1 text-xs leading-relaxed text-ink">{blast.summary}</p>
        </div>
        <button onClick={onClose} className="rounded p-1 text-muted hover:bg-line"><X size={16} /></button>
      </div>
      <Card className="mt-3 grid grid-cols-3 divide-x divide-line p-0 text-center">
        <Stat label="Down" value={blast.downCount} color={impactColor.Down} />
        <Stat label="Degraded" value={blast.degradedCount} color={impactColor.Degraded} />
        <Stat label="Affected" value={`${Math.round(blast.blastRatio * 100)}%`} color="var(--color-ink)" />
      </Card>
      {groups.map(([level, items]) => items.length > 0 && level !== 'Ok' && (
        <div key={level} className="mt-4">
          <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide" style={{ color: impactColor[level] }}>{level} ({items.length})</div>
          <ul className="space-y-1">
            {items.map(i => (
              <li key={i.componentId} className="rounded-md border border-line px-2 py-1.5 text-xs">
                <div className="font-medium">{i.name}</div>
                <div className="text-muted">{i.reason}</div>
              </li>
            ))}
          </ul>
        </div>
      ))}
      <div className="mt-4 text-[11px] text-muted">Deterministic graph propagation: synchronous callers without a circuit breaker go down; async and circuit-broken callers degrade. No AI involved.</div>
    </div>
  )
}

function Stat({ label, value, color }: { label: string; value: number | string; color: string }) {
  return <div className="py-2"><div className="text-xl font-bold" style={{ color }}>{value}</div><div className="text-[10px] uppercase tracking-wide text-muted">{label}</div></div>
}
