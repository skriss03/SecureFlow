import { useEffect, useRef } from 'react'
import { Loader2 } from 'lucide-react'
import type { LogEntry, ProjectStatus } from '../types'

export default function ProgressLog({ entries, status, error }: { entries: LogEntry[]; status: ProjectStatus; error?: string | null }) {
  const end = useRef<HTMLDivElement>(null)
  useEffect(() => { end.current?.scrollIntoView({ behavior: 'smooth' }) }, [entries.length])
  const running = status !== 'Ready' && status !== 'Error'
  const steps: ProjectStatus[] = ['Extracting', 'Analyzing', 'Reviewing', 'Ready']
  const idx = steps.indexOf(status === 'Queued' ? 'Extracting' : status)
  return (
    <div className="mx-auto mt-10 w-full max-w-3xl px-4">
      <div className="mb-4 flex items-center gap-2">
        {steps.map((s, i) => (
          <div key={s} className="flex items-center gap-2">
            <div className={`flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-medium ${i < idx ? 'bg-ok/20 text-ok' : i === idx && running ? 'bg-accent/20 text-accent' : i === idx ? 'bg-ok/20 text-ok' : 'bg-panel-2 text-muted'}`}>
              {i === idx && running && <Loader2 size={12} className="animate-spin" />}
              {s === 'Extracting' ? 'Extract model' : s === 'Analyzing' ? 'Run rules' : s === 'Reviewing' ? 'AI review' : 'Ready'}
            </div>
            {i < steps.length - 1 && <div className="h-px w-6 bg-line" />}
          </div>
        ))}
      </div>
      <div className="sf-scroll max-h-[60vh] overflow-auto rounded-xl border border-line bg-black/40 p-4 font-mono text-xs leading-relaxed">
        {entries.length === 0 && <div className="text-muted">Waiting for the pipeline…</div>}
        {entries.map((e, i) => (
          <div key={i} className={e.level === 'error' ? 'text-critical' : e.level === 'warn' ? 'text-medium' : 'text-ink'}>
            <span className="mr-2 text-muted">{new Date(e.at).toLocaleTimeString()}</span>{e.message}
          </div>
        ))}
        {error && <div className="mt-2 text-critical">Error: {error}</div>}
        <div ref={end} />
      </div>
    </div>
  )
}
