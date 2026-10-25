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

## Una spec por sesión
Cada spec se trabaja en **su propia sesión de IA, nueva**, de principio a fin
(SPECIFY → VERIFY → commit). Así se evita que una sesión arrastre contexto de otras specs,
y cada sesión queda como registro de una sola entrega. Al cerrar la spec:
1. `verify.md` completo y suites en verde.
2. Revisión de secretos y artefactos, commit y **push a `origin/main` (obligatorio: la siguiente spec no se abre con commits sin subir)**.
3. Actualizar el estado en el índice de abajo y en [roadmap.md](roadmap.md).
4. La siguiente spec se abre en una sesión nueva, que arranca leyendo [CLAUDE.md](../../CLAUDE.md).

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
| 005 | [Automatizaciones entre registros, cantidades e Inventario](specs/005-automatizaciones-inventario/spec.md) | Implementado |
| 006 | [Seguridad por campo y por registro](specs/006-seguridad-campos-registros/spec.md) | Implementado |
| 007 | [Metadatos de interfaz: layouts, vistas, reportes y tableros](specs/007-metadatos-interfaz/spec.md) | Implementado |
| 008 | [Integraciones: webhooks, claves de API, importación y exportación](specs/008-integraciones/spec.md) | Implementado |
| 009 | [Entornos: ciclo de vida de metadatos (sandbox → pruebas → producción)](specs/009-entornos/spec.md) | Implementado |
| 010 | [Multi-empresa y multi-moneda](specs/010-multi-empresa-moneda/spec.md) | Implementado |
| 011 | [Asistente IA (Claude)](specs/011-asistente-ia/spec.md) | Implementado |
| 012 | [Endurecimiento](specs/012-endurecimiento/spec.md) | Implementado |
| 013 | [Formulario en solo lectura](specs/013-formulario-solo-lectura/spec.md) | Implementado |
| 014 | [Pendientes menores y riesgos residuales](specs/014-pendientes-menores/spec.md) | Implementado |
| 015 | [Jerarquía de roles, equipos y unidades de negocio](specs/015-jerarquia-roles-equipos/spec.md) | Implementado |
| 016 | [Automatizaciones asíncronas y programadas](specs/016-automatizaciones-asincronas/spec.md) | Implementado |
| 017 | [Rollback de entornos](specs/017-rollback-entornos/spec.md) | Implementado |
| 018 | [OAuth2 (client credentials) y API OData](specs/018-oauth-odata/spec.md) | Implementado |
| 019 | [Editores visuales: diseñador del formulario y diagrama del proceso](specs/019-editores-visuales/spec.md) | Implementado |
| 020 | [Contabilidad y costeo (promedio y FIFO)](specs/020-contabilidad-costeo/spec.md) | Implementado |
| 021 | [Endurecimiento 2](specs/021-endurecimiento-2/spec.md) | Implementado |
| 022 | [Costeo 2: políticas de valuación configurables](specs/022-costeo-2/spec.md) | Implementado |
| 023 | [Capacidades del motor: listas que se fusionan, reglas de aviso y rutas de tres niveles](specs/023-capacidades-motor/spec.md) | Implementado |
| 024 | [Navegación por módulos](specs/024-navegacion-modulos/spec.md) | Implementado |
| 025 | [Explorador del modelo y de la base de datos](specs/025-explorador-modelo/spec.md) | Implementado |
| 026 | [Modelador por módulos y sin JSON](specs/026-modelador-visual/spec.md) | Implementado |
| 027 | [Documentos PDF con plantillas visuales](specs/027-documentos-pdf/spec.md) | Implementado |
