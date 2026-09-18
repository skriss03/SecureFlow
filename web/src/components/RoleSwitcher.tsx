import { useEffect, useRef, useState } from 'react'
import { ChevronDown } from 'lucide-react'
import { useRole } from '../role'
import type { Group } from '../types'

/** Dev-only: picks the local role and, for a Group Manager, which groups the dashboard shows. */
export default function RoleSwitcher({ groups }: { groups: Group[] }) {
  const { role, setRole, managedGroupIds, setManagedGroupIds } = useRole()
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const onDocClick = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [])

  const toggle = (id: string) =>
    setManagedGroupIds(managedGroupIds.includes(id) ? managedGroupIds.filter(g => g !== id) : [...managedGroupIds, id])

  return (
    <div className="flex items-center gap-1.5 text-xs">
      <div className="flex items-center gap-0.5 rounded-full border border-line bg-panel-2 p-0.5">
        {(['engineer', 'manager'] as const).map(r => (
          <button key={r} onClick={() => setRole(r)}
            className={`rounded-full px-2.5 py-1 transition ${role === r ? 'bg-accent font-semibold text-bg' : 'text-muted hover:text-ink'}`}>
            {r === 'engineer' ? 'Engineer' : 'Group Manager'}
          </button>
        ))}
      </div>

      {role === 'manager' && (
        <div ref={ref} className="relative">
          <button onClick={() => setOpen(o => !o)} title="Which groups this manager can see"
            className="flex items-center gap-1 rounded-full border border-line bg-panel-2 px-2.5 py-1 text-muted hover:text-ink">
            {managedGroupIds.length === 0 ? 'No groups' : `${managedGroupIds.length} group${managedGroupIds.length === 1 ? '' : 's'}`}
            <ChevronDown size={12} />
          </button>
          {open && (
            <div className="absolute right-0 z-30 mt-1 w-52 rounded-lg border border-line bg-panel p-1.5 shadow-lg">
              {groups.length === 0 && <div className="px-2 py-1 text-muted">No groups yet</div>}
              {groups.map(g => (
                <label key={g.id} className="flex cursor-pointer items-center gap-2 rounded px-2 py-1 hover:bg-panel-2">
                  <input type="checkbox" checked={managedGroupIds.includes(g.id)} onChange={() => toggle(g.id)} />
                  {g.name}
                </label>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
