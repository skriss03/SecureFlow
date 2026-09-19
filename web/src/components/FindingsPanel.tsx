import { useMemo, useState } from 'react'
import { Sparkles, Wrench } from 'lucide-react'
import type { ArchitectureModel, Finding, FindingCategory, FindingStatus, Severity } from '../types'
import { SeverityBadge, severityRank } from '../ui'

const SEVERITIES: Severity[] = ['Critical', 'High', 'Medium', 'Low', 'Info']

export default function FindingsPanel({ findings, model, selectedId, aiAvailable, onSelect, onFix }: {
  findings: Finding[]; model: ArchitectureModel; selectedId: string | null; aiAvailable: boolean
  onSelect: (f: Finding) => void; onFix: (f: Finding) => void
}) {
  const [category, setCategory] = useState<FindingCategory | 'All'>('All')
  const [status, setStatus] = useState<FindingStatus | 'All'>('Open')
  const [minSeverity, setMinSeverity] = useState<Severity>('Info')
  const [q, setQ] = useState('')

  const names = useMemo(() => new Map(model.components.map(c => [c.id, c.name])), [model])
  const rows = useMemo(() => findings
    .filter(f => (category === 'All' || f.category === category) && (status === 'All' || f.status === status) && severityRank[f.severity] >= severityRank[minSeverity])
    .filter(f => !q || (f.title + ' ' + f.description + ' ' + f.tag + ' ' + f.ruleId).toLowerCase().includes(q.toLowerCase()))
    .sort((a, b) => severityRank[b.severity] - severityRank[a.severity] || a.ruleId.localeCompare(b.ruleId)),
  [findings, category, status, minSeverity, q])

  const counts = useMemo(() => {
    const open = findings.filter(f => f.status === 'Open')
    return { open: open.length, fixed: findings.filter(f => f.status === 'Fixed').length, bySev: SEVERITIES.map(s => [s, open.filter(f => f.severity === s).length] as const) }
  }, [findings])

  const sel = 'rounded-md border border-line bg-panel-2 px-2 py-1 text-xs outline-none focus:border-accent'
  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line px-3 py-2">
        <select className={sel} value={category} onChange={e => setCategory(e.target.value as FindingCategory | 'All')}>
          <option value="All">Resilience + Security</option><option value="Resilience">Resilience</option><option value="Security">Security (STRIDE)</option>
        </select>
        <select className={sel} value={minSeverity} onChange={e => setMinSeverity(e.target.value as Severity)}>
          {SEVERITIES.map(s => <option key={s} value={s}>{s === 'Info' ? 'All severities' : `${s} and up`}</option>)}
        </select>
        <select className={sel} value={status} onChange={e => setStatus(e.target.value as FindingStatus | 'All')}>
          <option value="Open">Open ({counts.open})</option><option value="Fixed">Fixed ({counts.fixed})</option><option value="Accepted">Accepted</option><option value="All">All</option>
        </select>
        <input value={q} onChange={e => setQ(e.target.value)} placeholder="Search…" className={`${sel} min-w-[120px] flex-1`} />
        <div className="ml-auto flex items-center gap-1">
          {counts.bySev.filter(([, n]) => n > 0).map(([s, n]) => (
            <span key={s} className="flex items-center gap-1 text-[11px] text-muted"><SeverityBadge severity={s} small />{n}</span>
          ))}
        </div>
      </div>
      <div className="sf-scroll min-h-0 flex-1 overflow-auto">
        {rows.length === 0 && <div className="p-6 text-center text-sm text-muted">Nothing matches these filters.</div>}
        <ul>
          {rows.map(f => (
            <li key={f.id} onClick={() => onSelect(f)}
              className={`cursor-pointer border-b border-line px-3 py-2.5 hover:bg-panel ${selectedId === f.id ? 'bg-panel-2' : ''} ${f.status !== 'Open' ? 'opacity-60' : ''}`}>
              <div className="flex items-center gap-2">
                <SeverityBadge severity={f.severity} small />
                <span className={`text-sm font-medium ${f.status === 'Fixed' ? 'line-through' : ''}`}>{f.title}</span>
                <span className="ml-auto flex items-center gap-1.5">
                  {f.source === 'Ai' && <span className="flex items-center gap-1 text-[10px] text-accent"><Sparkles size={11} /> AI</span>}
                  {f.source === 'Rule' && <span className="font-mono text-[10px] text-muted">{f.ruleId}</span>}
                  {f.status === 'Open' && (
                    <button
                      onClick={e => { e.stopPropagation(); onFix(f) }}
                      disabled={!aiAvailable}
                      title={aiAvailable ? 'Propose a fix with AI' : 'Needs an AI provider'}
                      className="flex items-center gap-1 rounded-md border border-accent/40 bg-accent/10 px-1.5 py-0.5 text-[10px] font-medium text-accent hover:bg-accent/20 disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <Wrench size={10} /> Fix
                    </button>
                  )}
                </span>
              </div>
              <div className="mt-0.5 line-clamp-2 text-xs text-muted">{f.description}</div>
              <div className="mt-1 flex flex-wrap gap-1 text-[10px] text-muted">
                <span className="rounded bg-panel-2 px-1.5 py-0.5">{f.category === 'Security' ? `STRIDE · ${f.tag}` : f.tag}</span>
                {f.componentIds.slice(0, 3).map(id => <span key={id} className="rounded bg-panel-2 px-1.5 py-0.5">{names.get(id) ?? id}</span>)}
                {f.componentIds.length > 3 && <span>+{f.componentIds.length - 3}</span>}
                {f.confidence !== 'High' && <span className="italic">confidence {f.confidence.toLowerCase()}</span>}
              </div>
            </li>
          ))}
        </ul>
      </div>
    </div>
  )
}
