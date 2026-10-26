import { useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { flushSync } from 'react-dom';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Boxes, Eye, EyeOff, Info, Lock, LogIn, Moon, Sparkles, Sun, User, Workflow } from 'lucide-react';
import { useAuth } from '../auth/AuthContext';
import { messageFor, NOTICES, type Message } from '../auth/loginMessages';
import { BrandIllustration, BrandLogo } from '../components/Brand';
import { Alert, Button, Field, Input } from '../components/ui';
import { useTheme } from '../lib/theme';

const FEATURES = [
  { icon: Boxes, text: 'Catálogos y documentos declarados como datos' },
  { icon: Workflow, text: 'Procesos, reglas y fórmulas sin programar' },
  { icon: Sparkles, text: 'Un asistente de IA que entiende tu modelo' },
];

export function LoginPage() {
  const { me, login, loading, sessionEnd } = useAuth();
  const { theme, toggle } = useTheme();
  const navigate = useNavigate();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [capsLock, setCapsLock] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Message | null>(null);
  const [fieldErrors, setFieldErrors] = useState<{ username?: string; password?: string }>({});
  const [notice, setNotice] = useState<Message | null>(() => (sessionEnd ? NOTICES[sessionEnd.reason] : null));
  const userRef = useRef<HTMLInputElement>(null);
  const passwordRef = useRef<HTMLInputElement>(null);

  // F-03: tras una sesión que terminó, solo su dueño regresa a donde estaba; otra persona empieza en el inicio. Se toma al
  // montar: `login` limpia el motivo antes de que esta página redirija.
  const ended = useRef(sessionEnd).current;
  const from = (location.state as { from?: string } | null)?.from ?? '/';
  const destination = (username: string) => (ended && ended.username !== username ? '/' : from);
  if (!loading && me) return <Navigate to={destination(me.username)} replace />;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    const user = username.trim();
    const missing = { username: user ? undefined : 'Escribe tu usuario', password: password ? undefined : 'Escribe tu contraseña' };
    setFieldErrors(missing);
    if (missing.username || missing.password) {
      (missing.username ? userRef : passwordRef).current?.focus();
      return;
    }
    // Al enviar, la contraseña vuelve a ocultarse: el gestor de contraseñas la ve como contraseña. En este mismo evento,
    // no en el render siguiente (revisión adversarial F-04).
    flushSync(() => setShowPassword(false));
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const who = await login(user, password);
      navigate(destination(who.username), { replace: true });
    } catch (err) {
      setError(messageFor(err));
      setPassword('');
      // Tras un rechazo, el foco va a la contraseña, que es lo que hay que volver a escribir.
      passwordRef.current?.focus();
    } finally {
      setBusy(false);
    }
  }

  const onPasswordKey = (e: KeyboardEvent<HTMLInputElement>) => setCapsLock(e.getModifierState?.('CapsLock') ?? false);
  const passwordHelp = [capsLock && 'login-caps', error && 'login-error', fieldErrors.password && 'login-password-err'].filter(Boolean).join(' ');
  // F-06: el aviso que ya está al cargar (sesión expirada o cerrada) se lee con el campo que recibe el foco.
  const userHelp = [notice && !error && 'login-notice', error && 'login-error', fieldErrors.username && 'login-username-err'].filter(Boolean).join(' ');

  return (
    <div className="login-shell">
      <aside className="login-aside" aria-label="Acerca de ERP AI">
        <BrandLogo size={36} />
        <div className="login-aside-art">
          <BrandIllustration />
        </div>
        <div className="login-aside-copy">
          <p className="login-tagline">Tu ERP, declarado como datos.</p>
          <ul className="login-features">
            {FEATURES.map(({ icon: Icon, text }) => (
              <li key={text}><Icon size={16} aria-hidden />{text}</li>
            ))}
          </ul>
        </div>
      </aside>

      <main className="login-main">
        <div className="login-topbar">
          <Button variant="ghost" size="sm" iconOnly icon={theme === 'dark' ? Sun : Moon}
            aria-label={theme === 'dark' ? 'Usar tema claro' : 'Usar tema oscuro'} onClick={toggle} />
        </div>
        <div className="login-panel">
          <div className="login-compact-logo"><BrandLogo size={40} /></div>
          <header className="login-header">
            <h1>Inicia sesión</h1>
            <p className="muted">Usa tu cuenta de ERP AI para continuar.</p>
          </header>

          <form className="login-form" onSubmit={onSubmit} noValidate>
            {notice && !error && <div className="login-message" id="login-notice"><Alert tone="info" icon={Info} title={notice.title}>{notice.detail}</Alert></div>}
            {error && <div className="login-message" id="login-error"><Alert tone="danger" title={error.title}>{error.detail}</Alert></div>}
            <Input
              ref={userRef}
              id="login-username"
              label="Usuario"
              icon={User}
              autoComplete="username"
              autoCapitalize="none"
              spellCheck={false}
              autoFocus
              value={username}
              onChange={(e) => { setUsername(e.target.value); if (fieldErrors.username) setFieldErrors((f) => ({ ...f, username: undefined })); }}
              maxLength={100}
              error={fieldErrors.username}
              aria-describedby={userHelp || undefined}
            />
            <Field label="Contraseña" htmlFor="login-password" error={fieldErrors.password}>
              <div className="input-wrap login-password">
                <Lock size={16} aria-hidden />
                <input
                  ref={passwordRef}
                  id="login-password"
                  className="input"
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="current-password"
                  autoCapitalize="none"
                  spellCheck={false}
                  value={password}
                  onChange={(e) => { setPassword(e.target.value); if (fieldErrors.password) setFieldErrors((f) => ({ ...f, password: undefined })); }}
                  onKeyDown={onPasswordKey}
                  onKeyUp={onPasswordKey}
                  onBlur={() => setCapsLock(false)}
                  maxLength={256}
                  aria-invalid={fieldErrors.password ? true : undefined}
                  aria-describedby={passwordHelp || undefined}
                />
                <button type="button" className="login-reveal" aria-pressed={showPassword} aria-controls="login-password"
                  aria-label={showPassword ? 'Ocultar contraseña' : 'Mostrar contraseña'}
                  title={showPassword ? 'Ocultar contraseña' : 'Mostrar contraseña'}
                  onClick={() => setShowPassword((v) => !v)}>
                  {showPassword ? <EyeOff size={16} aria-hidden /> : <Eye size={16} aria-hidden />}
                </button>
              </div>
              {/* Región viva siempre presente (vacía si no aplica): así el lector de pantalla anuncia el cambio. */}
              <p id="login-caps" className="login-caps" role="status">{capsLock ? 'Bloq Mayús está activado' : ''}</p>
            </Field>
            <Button type="submit" variant="primary" size="lg" block loading={busy} icon={LogIn}>{busy ? 'Entrando…' : 'Entrar'}</Button>
          </form>

          <p className="login-help">¿Olvidaste tu contraseña? Pide a un administrador que la restablezca.</p>
        </div>
      </main>
    </div>
  );
}
