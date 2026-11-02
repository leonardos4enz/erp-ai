# Tareas 047 — Acompañante: panel lateral con avatar

- [x] T1 Spec, plan (STRIDE) y tareas; CI de la 046 anotada en su `verify.md`.
- [x] T2 Chat compartido: `useAgentChat` (genérico) con `useStudioChat` como envoltura; `Composer` con etiqueta e `id` propios;
  `ToolGroup` con nombres para personas y sin entrada/salida; `groupTurns` con el paso `action`; `liveLabel` con nombres.
- [x] T3 `companionApi` y tipos (`segunElAcompanante`, paso `action`).
- [x] T4 `lib/companion.ts`: estado del avatar, vista de la acción, contexto de la pantalla, nombres de herramientas, avisos.
- [x] T5 `CompanionAvatar.tsx` y `styles/companion.css` (port de `<fleet-eyes>`, estados del chat, tokens, movimiento reducido).
- [x] T6 `CompanionPanel.tsx`: panel en el `Shell` y botón en la barra superior; estados cargando, error, no disponible,
  bienvenida, historial (retomar y borrar) y conversación.
- [x] T7 `ActionCard.tsx`: vista previa, explicación aparte, confirmar con huella, avisos (428), descartar, estados.
- [x] T8 Pruebas: `companion.test.ts` (Vitest), `acompanante.spec.ts` (E2E por criterio y capturas), capturas de la barra
  superior revisadas.
- [x] T9 VERIFY: revisión adversarial y exploratoria, suites, `verify.md`, `riesgos.md`, roadmap.
