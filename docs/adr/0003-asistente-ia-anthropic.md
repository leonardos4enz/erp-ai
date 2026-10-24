# ADR-0003 — Asistente IA con Claude (Anthropic) en lugar de MiniMax

**Estado:** Aceptado · **Fecha:** 2026-10-23

## Contexto
La constitución (II.7, II.8) y el roadmap preveían un asistente con MiniMax. Al arrancar la spec 011 el dueño del
proyecto pidió usar la API de Anthropic. La constitución nombraba a MiniMax solo como ejemplo de secreto: los principios
(la IA solo propone Planes de Cambios, su salida no es confiable, la clave fuera del repositorio) no dependen del proveedor.

## Decisión
- Proveedor: **Anthropic**, por el SDK oficial para .NET (`Anthropic` en NuGet), modelo `claude-sonnet-5-5` por defecto (elegido por el dueño para cuidar el costo; Opus es configurable)
  (`Assistant:Model`), con salida estructurada (esquema JSON `{ explanation, plan }`) y sin herramientas.
- Clave: la captura un administrador en *Configuración › Asistente IA* (pedido del dueño): se prueba con el proveedor,
  se guarda cifrada con Data Protection y no se vuelve a mostrar. Opcionalmente el servidor la trae por configuración
  (`Assistant:AnthropicApiKey` en user-secrets o `Assistant__AnthropicApiKey`), y entonces manda. Siempre se pasa
  explícita al SDK, que así no toma credenciales del equipo.
- El proveedor queda detrás de `IAssistantModel`: cambiarlo no toca el resto del motor. Para pruebas, `Assistant:BaseUrl`
  apunta a un proveedor simulado en loopback (fuera de loopback exige https).
- La constitución cambia el ejemplo de II.7 de «MiniMax» a «Anthropic» y agrega que un secreto puede guardarse cifrado
  en la base con Data Protection (ya era el caso de los secretos de webhooks).

## Consecuencias
- (+) SDK oficial mantenido por el proveedor, salida estructurada y contexto amplio (cabe el modelo completo de metadatos).
- (+) Las pruebas no dependen del proveedor ni de una clave real: el simulado responde con el mismo formato de la API.
- (−) Cada solicitud envía a un tercero la estructura del modelo (nunca datos) y el texto que escribe la persona: se
  advierte en la pantalla y queda en la auditoría.
- (−) Costo por uso: se limita por usuario y por hora, y por tamaño de respuesta.
- El SDK (v13) envía la clave como `Authorization: Bearer`; no se registra en logs, auditoría ni respuestas.
