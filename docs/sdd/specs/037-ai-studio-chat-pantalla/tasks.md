# Tareas 037

## Abrir
- [x] `git rev-list --count origin/main..main` = 0; CI de `a1faebf` («Spec 036») en verde (anotado en `verify.md`).
- [x] SPECIFY y CLARIFY (`spec.md`): no se divide; el motor no cambia.
- [x] PLAN (`plan.md`) con STRIDE de 8 filas.

## Lógica sin DOM (Vitest)
- [x] `types.ts`: campos de la 036 (`turns`, `planStatus`, `canSend`, `sendBlockedReason`, `updatedAt`, `maxTurns`) y eventos.
- [x] `lib/studio.ts`: `groupTurns`, `currentProposalSeq`, `createSseParser`, `revealStep`, `nextBackoff`, `relativeTime`,
      `consumption` con mensajes.
- [x] `lib/markdown.ts`: subconjunto seguro (bloques e inline), lineal.
- [x] Vitest: `studio.test.ts`, `markdown.test.ts` (con la guardia de `dangerouslySetInnerHTML`).

## Pantalla
- [x] `studioApi.send` y `streamUrl`; hook `useStudioChat` (lectura, flujo, reconexión, envío, detener, aplicar).
- [x] `StudioPage` (barra, cabecera, bienvenida, conversación), `pages/studio/*` (Composer, Turn, ToolGroup, Markdown,
      LiveText, ProposalCard, ConsumptionPanel).
- [x] `styles/studio.css` (claro/oscuro/móvil, movimiento reducido); quitar `.studio-*` de `app.css`.

## Pruebas
- [x] `e2e/studio.spec.ts` reescrito por criterio (H1–H6) y capturas nuevas revisadas una por una; `pantallas.test.ts`.
- [x] Lo nuevo con `--veces=3` (lento); corrida completa; `dotnet test`.

## VERIFY
- [x] Revisión adversarial (subagente sin contexto: diff + STRIDE).
- [x] Revisión exploratoria en el navegador (claro, oscuro, 1280/390 px, teclado).
- [x] `verify.md`, `riesgos.md`, roadmap, índice; commit y push; CI.
