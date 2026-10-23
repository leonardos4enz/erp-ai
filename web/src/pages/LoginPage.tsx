import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Lock, LogIn, User } from 'lucide-react';
import { ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Alert, Button, Input } from '../components/ui';

function messageFor(err: unknown): { title: string; detail?: string } {
  if (err instanceof ApiError) {
    switch (err.status) {
      case 401: return { title: 'Usuario o contraseña incorrectos' };
      case 423: return { title: 'Cuenta bloqueada temporalmente', detail: err.detail ?? 'Demasiados intentos fallidos. Intenta de nuevo en unos minutos o contacta a un administrador.' };
      case 429: return { title: 'Demasiados intentos', detail: 'Espera un momento antes de volver a intentar.' };
      case 0: return { title: 'Sin conexión con el servidor', detail: 'Verifica que la API esté en ejecución.' };
      default: return { title: err.title, detail: err.detail };
    }
  }
  return { title: 'No se pudo iniciar sesión' };
}

export function LoginPage() {
  const { me, login, loading } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ title: string; detail?: string } | null>(null);

  const from = (location.state as { from?: string } | null)?.from ?? '/';
  if (!loading && me) return <Navigate to={from} replace />;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (!username.trim() || !password) {
      setError({ title: 'Escribe tu usuario y contraseña' });
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await login(username.trim(), password);
      navigate(from, { replace: true });
    } catch (err) {
      setError(messageFor(err));
      setPassword('');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-page">
      <div className="card login-card">
        <div className="login-brand">
          <div className="brand-mark" aria-hidden>
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <path d="M5 7h14M5 12h10M5 17h14" />
            </svg>
          </div>
          <div>
            <h1 style={{ fontSize: 'var(--text-xl)', fontWeight: 600, letterSpacing: '-0.015em' }}>ERP AI</h1>
            <p className="muted">Inicia sesión para continuar</p>
          </div>
        </div>
        <form className="stack" onSubmit={onSubmit} noValidate>
          {error && <Alert tone="danger" title={error.title}>{error.detail}</Alert>}
          <Input
            label="Usuario"
            icon={User}
            autoComplete="username"
            autoFocus
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            maxLength={100}
          />
          <Input
            label="Contraseña"
            icon={Lock}
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            maxLength={256}
          />
          <Button type="submit" variant="primary" size="lg" block loading={busy} icon={LogIn}>Entrar</Button>
        </form>
      </div>
    </div>
  );
}
