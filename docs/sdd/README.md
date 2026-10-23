# Metodología: Spec-Driven Development (SDD) asistido por IA

Elegimos **SDD** (al estilo de GitHub Spec Kit) combinado con **pruebas primero en código
crítico** y **ADRs**. Es la metodología que mejor funciona con agentes de IA porque la IA
trabaja contra un documento verificable y no contra una conversación que se olvida.

## Flujo por cada funcionalidad

```
constitution.md  (principios fijos, se lee siempre)
      │
 1. SPECIFY   specs/NNN-nombre/spec.md    QUÉ y POR QUÉ. Historias, criterios de aceptación.
      │                                    Sin tecnología.
 2. CLARIFY   sección "Preguntas abiertas" del spec, resueltas por el humano.
      │
 3. PLAN      specs/NNN-nombre/plan.md    CÓMO. Arquitectura, contratos de API, modelo
      │                                    de datos, análisis de amenazas (STRIDE).
 4. TASKS     specs/NNN-nombre/tasks.md   Tareas pequeñas, ordenadas, cada una verificable.
      │
 5. IMPLEMENT la IA ejecuta tarea por tarea, marcando [x].
      │
 6. VERIFY    specs/NNN-nombre/verify.md   Ver verificacion.md:
                 a) unitarias  b) E2E Playwright (API, criterios, a11y, visual)
                 c) revisión exploratoria con agente → cada hallazgo se vuelve prueba
```

## Reglas para agentes de IA
- Leer `constitution.md` y el `spec.md` vigente antes de escribir código.
- No implementar nada que no esté en `tasks.md`. Si falta algo, proponer cambio al spec.
- Si el spec es ambiguo, preguntar (fase CLARIFY) en vez de suponer.
- Cada tarea termina con build verde y pruebas pasando.
- Cada criterio de aceptación del spec tiene al menos un escenario E2E (`H1 CA3: …`).
- Antes de cerrar, la revisión exploratoria se hace usando la app en el navegador, no leyendo código.

## Índice de specs
| # | Spec | Estado |
|---|------|--------|
| 001 | [Núcleo de metadatos, API genérica y UI dinámica](specs/001-nucleo-metadatos/spec.md) | Implementado (MVP) |
| 002 | [Motor: documentos, fórmulas, reglas y procesos](specs/002-motor-procesos/spec.md) | Implementado |
| 003 | [Módulo de Compras](specs/003-modulo-compras/spec.md) | Implementado |
| 004 | [Campos asignados por el sistema y actualización de módulos](specs/004-campos-sistema-y-actualizaciones/spec.md) | Implementado |
| 005–011 | Automatizaciones, seguridad por campo/registro, vistas y reportes, integraciones, entornos, multi-empresa, IA | [Roadmap](roadmap.md) |
