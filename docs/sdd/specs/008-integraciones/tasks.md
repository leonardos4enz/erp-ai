# Tareas 008

## Motor
- [x] Modelos: `WebhookDef`, `SetWebhooksOp`; `ModuleUpgrade.Roles`
- [x] Esquema: `WebhooksJson`, `User.IsService`, `meta.ApiKey`, `meta.WebhookEndpoint`, `meta.WebhookDelivery`; carga y guardado
- [x] `WebhookRules` en el compilador (y revalidación en la pasada final)
- [x] `WebhookUrlPolicy`, `WebhookSignature`, `WebhookSender` (ConnectCallback), `WebhookDispatcher` (outbox, reintentos, purga)
- [x] `RecordService.EnqueueWebhooksAsync` desde `FireAsync`, con la seguridad del rol de servicio
- [x] Claves de API: `ApiKeyHandler`, esquema de política, lista blanca de rutas, CSRF, cuentas de servicio (login, usuarios, buscador)
- [x] Endpoints de administración de webhooks y claves (superusuario, auditados)
- [x] `ImportReader` (CSV y XLSX) e `RecordService.ImportAsync` (vista previa y todo o nada); exportación de listados
- [x] Unitarias: política de URL, firma, claves, lectores CSV/XLSX, conversión de celdas, reglas de webhooks, módulos

## Paquetes
- [x] Roles en actualizaciones de módulo (instalación y actualización)
- [x] Compras 1.5.0 e Inventario 1.3.0 en `upgrades`

## UI
- [x] Tipos y endpoints (cuerpo binario para importar)
- [x] Listado: Exportar e Importar (plantilla, vista previa, errores por fila)
- [x] Integraciones: webhooks (configurar, probar, rotar, entregas, reintentar) y claves de API
- [x] Modelador: Webhooks (JSON)

## Verificación
- [x] E2E por criterio (`integraciones.spec.ts`) con receptor HTTP local que verifica la firma
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Actualización real Compras 1.4.0 → 1.5.0 e Inventario 1.2.0 → 1.3.0 en `ErpAi`, revisada en navegador
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit y push
