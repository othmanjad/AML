import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, onUnauthorized, TOKEN_KEY, getToken } from '../api/client';
import type { Me } from '../api/types';

interface AuthState {
  me: Me | null;
  loading: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  can: (perm: string | string[] | undefined) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState<boolean>(!!getToken());

  const clear = useCallback(() => {
    try {
      sessionStorage.removeItem(TOKEN_KEY);
    } catch {
      /* ignore */
    }
    setMe(null);
  }, []);

  const loadMe = useCallback(async () => {
    setLoading(true);
    try {
      setMe(await api.get<Me>('/api/auth/me'));
    } catch {
      clear();
    } finally {
      setLoading(false);
    }
  }, [clear]);

  useEffect(() => {
    onUnauthorized(clear);
    if (getToken()) void loadMe();
  }, [clear, loadMe]);

  const login = useCallback(
    async (username: string, password: string) => {
      const r = await api.post<{ accessToken: string }>('/api/auth/login', { username, password });
      sessionStorage.setItem(TOKEN_KEY, r.accessToken);
      await loadMe();
    },
    [loadMe],
  );

  const logout = useCallback(async () => {
    try {
      await api.post('/api/auth/logout');
    } catch {
      /* token may already be invalid */
    }
    clear();
  }, [clear]);

  const can = useCallback(
    (perm: string | string[] | undefined) => {
      if (!perm) return true;
      if (!me) return false;
      const list = Array.isArray(perm) ? perm : [perm];
      return list.some((p) => me.permissions.includes(p));
    },
    [me],
  );

  const value = useMemo(() => ({ me, loading, login, logout, can }), [me, loading, login, logout, can]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export const useAuth = (): AuthState => {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
};
