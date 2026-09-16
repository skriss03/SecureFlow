import { useEffect, useState } from 'react'
import { Link, Route, Routes } from 'react-router-dom'
import { ShieldCheck, Sparkles, WifiOff } from 'lucide-react'
import { api } from './api'
import type { Health } from './types'
import { Pill } from './ui'
import Home from './pages/Home'
import ProjectPage from './pages/ProjectPage'

export default function App() {
  const [health, setHealth] = useState<Health | null>(null)
  useEffect(() => {
    api.health().then(setHealth).catch(() => setHealth(null))
  }, [])

  return (
    <div className="flex h-full flex-col">
      <header className="no-print flex items-center justify-between border-b border-line bg-panel px-5 py-2.5">
        <Link to="/" className="flex items-center gap-2.5">
          <span className="grid h-8 w-8 place-items-center rounded-lg bg-accent/15 text-accent"><ShieldCheck size={20} /></span>
          <span className="text-base font-semibold tracking-tight">SecureFlow</span>
          <span className="hidden text-xs text-muted sm:inline">AI resilience &amp; threat modeling</span>
        </Link>
        <div className="flex items-center gap-2">
          {health === null ? (
            <Pill tone="danger"><WifiOff size={12} /> API offline</Pill>
          ) : health.ai.available ? (
            <Pill tone="accent" title={health.offline ? 'Serving AI responses from the replay cache only' : undefined}>
              <Sparkles size={12} /> {health.ai.provider}{health.offline ? ' · replay' : ''}
            </Pill>
          ) : (
            <Pill tone="warn" title={health.ai.error ?? ''}>Rules only · no AI key</Pill>
          )}
        </div>
      </header>
      <main className="min-h-0 flex-1">
        <Routes>
          <Route path="/" element={<Home aiAvailable={health?.ai.available ?? false} />} />
          <Route path="/p/:id" element={<ProjectPage aiAvailable={health?.ai.available ?? false} />} />
        </Routes>
      </main>
    </div>
  )
}
