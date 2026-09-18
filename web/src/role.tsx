import { createContext, useContext, useState, type ReactNode } from 'react'

// Dev-only stand-in for authentication. There is no login: the current role and the groups a
// manager sees are picked from the header and kept in localStorage, purely to drive which UI
// shows and which groupIds the dashboard asks for. Nothing here is enforced server-side.
export type Role = 'engineer' | 'manager'

interface RoleState {
  role: Role
  setRole: (r: Role) => void
  managedGroupIds: string[]
  setManagedGroupIds: (ids: string[]) => void
}

const RoleContext = createContext<RoleState | null>(null)
const ROLE_KEY = 'secureflow.devRole'
const GROUPS_KEY = 'secureflow.devManagedGroupIds'

export function RoleProvider({ children }: { children: ReactNode }) {
  const [role, setRoleState] = useState<Role>(() => (localStorage.getItem(ROLE_KEY) as Role) || 'engineer')
  const [managedGroupIds, setIds] = useState<string[]>(() => {
    try { return JSON.parse(localStorage.getItem(GROUPS_KEY) || '[]') } catch { return [] }
  })

  const setRole = (r: Role) => { setRoleState(r); localStorage.setItem(ROLE_KEY, r) }
  const setManagedGroupIds = (ids: string[]) => { setIds(ids); localStorage.setItem(GROUPS_KEY, JSON.stringify(ids)) }

  return <RoleContext.Provider value={{ role, setRole, managedGroupIds, setManagedGroupIds }}>{children}</RoleContext.Provider>
}

export function useRole() {
  const ctx = useContext(RoleContext)
  if (!ctx) throw new Error('useRole must be used within RoleProvider')
  return ctx
}
