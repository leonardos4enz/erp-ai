import { ApiError } from '../api/client';

export type Message = { title: string; detail?: string };

/**
 * Spec 032 (H4): un mensaje por estado, escrito para la persona y nunca con el texto del servidor. El 401 es el mismo para
 * un usuario que no existe, una contraseña incorrecta, una cuenta inactiva o bloqueada (001 F-09): el servidor no los
 * distingue y la pantalla tampoco. El 429 y el bloqueo no dan cifras.
 */
export function messageFor(err: unknown): Message {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return {
        title: 'Usuario o contraseña incorrectos',
        detail: 'Si fallaste varias veces, la cuenta se bloquea por unos minutos: espera o pide ayuda a un administrador.',
      };
    }
    if (err.status === 429) return { title: 'Demasiados intentos', detail: 'Espera un momento antes de volver a intentarlo.' };
    if (err.status === 0 || err.status >= 500) {
      return { title: 'No pudimos conectar con el servidor', detail: 'Revisa tu conexión e inténtalo de nuevo en unos momentos.' };
    }
  }
  return { title: 'No se pudo iniciar sesión', detail: 'Inténtalo de nuevo. Si el problema sigue, avisa a un administrador.' };
}

export const NOTICES: Record<'expired' | 'signedOut', Message> = {
  expired: { title: 'Tu sesión expiró', detail: 'Vuelve a iniciar sesión para continuar donde estabas.' },
  signedOut: { title: 'Cerraste sesión', detail: 'Hasta pronto. Puedes volver a entrar cuando quieras.' },
};
