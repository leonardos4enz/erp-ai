# Plan NNN — Diseño técnico

<!-- Plantilla (spec 028). Copiar a docs/sdd/specs/NNN-nombre/plan.md y borrar los comentarios. Los enlaces son relativos a esa carpeta. -->

## 1. Modelo de datos
<!-- Tablas y columnas nuevas (en Bootstrap), metadatos nuevos (operaciones del Plan de Cambios). -->

## 2. Contrato de la API
<!-- Cada ruta: método, ruta, capacidad que exige, cuerpo, respuestas (200/400/403/404/409). El backend lo cumple al pie
     de la letra (CLAUDE.md). -->
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|

## 3. SPA
<!-- Pantallas, componentes, accesibilidad (teclado, foco, anuncios), estados vacío/carga/error. -->

## 4. Pruebas
<!-- Unitarias, E2E por criterio, regresiones; qué archivos son nuevos (irán con --veces=3 al cerrar, junto con los archivos de los que dependen). -->

## 5. STRIDE
**Las 8 filas son obligatorias.** Si una no aplica, se dice por qué. Cada mitigación cita su prueba (archivo › título);
sin prueba, la amenaza va a [riesgos.md](../../riesgos.md) con su ID (`R-NNN-k`) y no cuenta como mitigada. «Revisión de
código» no basta para una amenaza alta.

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones de plan, acciones de automatización, opciones de OData o caminos de módulo/paquete/asistente agrega o cambia? ¿Qué capacidad pide cada una? | | |
| Caminos alternos | ¿Por qué otros caminos se llega al mismo dato o efecto (UI, API, importación, exportación, OData, webhooks, automatización, tarea, paquete, reversión, impresión, asistente, módulo)? ¿Todos aplican la misma regla? | | |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | | |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? ¿El beneficiario puede aprobarse? | | |
| Concurrencia | ¿Qué invariante entre filas protege? ¿Qué pasa con 10 peticiones a la vez? ¿Hay «revisar → aplicar» o «abrir → guardar» sin huella? | | |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos (commit y marca, reclamo y aplicación)? ¿Se repite, se pierde o se atora? | | |
| Límites | Tamaño, paginación, `$skip`, `$expand`, topes en memoria, tasa por usuario o IP | | |
| Semántica | Nulos y vacíos, monedas, fechas con zona, redondeo | | |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | | | |
