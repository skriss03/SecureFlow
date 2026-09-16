import { useEffect, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Download, Loader2, Printer } from 'lucide-react'
import { api } from '../api'
import { Button } from '../ui'

export default function ReportView({ projectId, version }: { projectId: string; version: number }) {
  const [md, setMd] = useState<string | null>(null)
  useEffect(() => { setMd(null); api.report(projectId).then(setMd) }, [projectId, version])
  return (
    <div className="sf-scroll h-full overflow-auto">
      <div className="mx-auto max-w-4xl px-6 py-6">
        <div className="no-print mb-4 flex items-center gap-2">
          <Button variant="ghost" onClick={() => window.print()}><Printer size={14} /> Print / Save as PDF</Button>
          <a href={api.reportUrl(projectId)} download={`secureflow-report-${projectId}.md`} className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-sm hover:bg-line"><Download size={14} /> Markdown</a>
          <a href={api.modelUrl(projectId)} download={`secureflow-model-${projectId}.json`} className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-3 py-1.5 text-sm hover:bg-line"><Download size={14} /> Model JSON</a>
        </div>
        {md === null ? (
          <div className="flex items-center gap-2 text-muted"><Loader2 size={16} className="animate-spin" /> Building report…</div>
        ) : (
          <article className="prose-report"><ReactMarkdown remarkPlugins={[remarkGfm]}>{md}</ReactMarkdown></article>
        )}
      </div>
    </div>
  )
}
