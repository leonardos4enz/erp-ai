# ADR-0001 — Tablas reales generadas por DDL en lugar de EAV

**Estado:** Aceptado · **Fecha:** 2026-10-23

## Contexto
Los catálogos se definen dinámicamente. Opciones: (a) EAV (una tabla entidad-atributo-valor),
(b) JSON por registro, (c) generar tablas reales con `CREATE/ALTER TABLE`.

## Decisión
Generar tablas reales en el esquema `app`. Los metadatos viven en el esquema `meta`.

## Consecuencias
- (+) Índices, llaves foráneas, tipos nativos, rendimiento y reportes con SQL estándar.
- (+) Integridad referencial real entre catálogos.
- (−) Requiere que la cuenta de la aplicación tenga permisos DDL sobre el esquema `app`.
  En producción: un login dedicado con `ALTER` y `CREATE TABLE` solo sobre `app`,
  `SELECT/INSERT/UPDATE` en `meta` y **sin** `DELETE`/`UPDATE` sobre `meta.AuditLog`.
- (−) Toda generación de SQL debe pasar por `Identifiers` y `DdlGenerator` (constitución II.1).
- Las columnas dinámicas son `NULL`; la obligatoriedad la aplica el motor para poder agregar
  campos a tablas con datos.
