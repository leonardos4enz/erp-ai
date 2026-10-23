# Verificación 010 — Multi-empresa y multi-moneda

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 259/259 (+10 en `CompanyTests`: DDL de un catálogo por empresa —columna, FK, índice, únicos compuestos, folio global—, pasar a por empresa —empresa predeterminada, índices rehechos, idempotente—, volver a compartido y partidas rechazados, `CompanyId` reservado, fórmulas con la empresa en catálogos compartidos, filtro de empresa —también superusuario, combinado con 006, heredado por partidas, prefijado en reportes—, `RATE`/`CONVERT`/`BASECURRENCY`/`CURRENTCOMPANY` con nulos y mayúsculas, analizador, diff de paquetes, Compras 1.6.0 + Inventario 1.4.0). La ida y vuelta de `EnvironmentTests` sigue con diff vacío |
| E2E | 145/145 en dos corridas limpias seguidas (+11 en `multiempresa.spec.ts`, que corre después de Inventario y desactiva sus empresas al final). Se ajustaron dos pruebas previas por el cambio intencional: `integraciones › H5` (Compras instalada ahora es 1.6.0) y `seguridad › H4 CA3` (la etiqueta «Total MXN» pasó a «Total en moneda base») |
| Visual | Capturas de referencia sin cambios: con una sola empresa no aparece el selector; «Empresas» va después de «Entornos»; «Un registro por empresa» vive en la sección avanzada del modelador (cerrada en la captura). La corrección F-02 oculta las migas intermedias en móvil: `formulario-movil.png` sigue dentro de la tolerancia y no se aprobó ninguna referencia nueva |
| Base real | `ErpAi`: Compras **1.5.0 → 1.6.0** e Inventario **1.3.0 → 1.4.0** desde la UI, con vista previa válida («'Almacén' pasa a un registro por empresa (los registros existentes quedan en la empresa predeterminada)»…); la migración creó «Empresa principal» (MXN) con los 2 usuarios |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 — alta y edición de empresas, validación, clave fija, predeterminada no se desactiva, solo superusuario, auditoría | `multiempresa › H1 CA1` |
| H1 CA2 — empresas en el diálogo de usuario, predeterminada al crear sin indicarlas, sesiones revocadas, auditoría | `multiempresa › preparar…`, `H1 CA2` |
| H1 CA3 + H2 CA1 — una empresa: sin selector; varias: selector con teclado (Enter, flechas, Esc), elección recordada, empresa en el menú de usuario, a11y | `multiempresa › H1 CA3 + H2 CA1` |
| H2 CA2 — solo empresas propias y activas; al desactivar la actual queda en otra | `multiempresa › H2 CA2` |
| H2 CA3 + H3 CA2 · CA3 · CA4 — 404 en otra empresa (GET, PUT, DELETE, buscador, referencias; también superusuario), `CompanyId` no aceptado, únicos por empresa, compartidos visibles, reportes, exportación e importación en la empresa actual | `multiempresa › H3 CA2 · CA3 · CA4`; unitaria `El_filtro_de_empresa…` |
| H3 CA1 · CA5 — modelador: «Un registro por empresa», vista previa, existentes en la predeterminada, sin vuelta atrás | `multiempresa › H3 CA1 · CA5`; unitarias `Pasar_a_por_empresa…`, `Volver_a_compartido…` |
| H3 CA6 — viaja en paquetes y el diff lo compara | `multiempresa › H3 CA6`; unitaria `El_diff_lleva_por_empresa…` |
| H4 CA1 — tipos de cambio: registrar, reemplazar el del día, filtrar, eliminar con confirmación, auditoría, solo superusuario | `multiempresa › H4 CA1` |
| H4 CA2 · CA4 — sugerencia en vivo por fecha y moneda base, inverso, sin tipo = vacío y obligatorio, captura manual respetada | `multiempresa › H4 CA2 · CA4`; unitaria `Funciones_de_empresa…` |
| H4 CA3 — recálculo con la empresa de cada registro; rechazo en catálogos compartidos | `multiempresa › H4 CA3`; unitaria `Una_formula_guardada…` |
| H5 CA1–CA4 — Compras 1.6.0 e Inventario 1.4.0: catálogos por empresa, tipo de cambio sugerido, recepción del Norte mueve solo existencias del Norte, lo existente en la predeterminada | `multiempresa › H5 …`; unitaria `Compras_e_Inventario…` |
| Móvil sin desbordes (320–760 px) | `multiempresa › Empresas y selector sin desbordes…` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Baja | (exploratoria, `ErpAi`) La tarjeta «Registrar tipo de cambio» no tenía relleno: el título y el texto tocaban el borde | Corregido (`card-pad`); regresión en `multiempresa › H4 CA1`, **verificada fallando con el código anterior** (relleno 0) |
| F-02 | Media | (exploratoria, 375 px) Con el selector de empresa en la barra, las migas de pan se encimaban («InicioConfiguracióEm…») | Corregido (en ≤ 600 px solo se muestra la miga actual); regresión en `multiempresa › Empresas y selector sin desbordes…`, **verificada fallando con el código anterior** (a 320 px, «Inicio» y «Notas ME» recortadas) |

Durante la implementación (revisión de código, antes de la exploratoria) se encontró que los datos iniciales de un módulo
resolvían `$ref` y su clave sin la empresa: se corrigió `ModuleService` para buscarlos en la empresa de quien instala.

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- *Módulos*: vistas previas de Compras 1.6.0 (11 pasos, SQL de cada «por empresa») e Inventario 1.4.0; aplicadas sin errores.
- *Configuración › Empresas*: «Empresa principal · PRINCIPAL · MXN · 2 usuarios · Activa · Predeterminada». Se registró USD → MXN
  18,25 (la coma decimal se acepta) para hoy; ahí se encontró F-01.
- Orden de compra nueva con *Industrial Supply* (USD): moneda y **tipo de cambio 18.25 sugeridos solos** (no se guardó).
- Empresa temporal «Compañía de demostración… — ñandú» (USD): el selector aparece, el nombre largo se ajusta en el menú; en
  ella *Órdenes de compra* está vacío y el tablero *Compras* muestra 0 y «Monto comprado (moneda base)». En móvil se
  encontró F-02. Al terminar se volvió a «Empresa principal» y la empresa temporal quedó **desactivada** (sin datos).
- Consola sin errores ni violaciones de CSP. No se modificó al usuario `admin` (solo su empresa actual, que volvió a la principal).

## Riesgo residual
- Folios únicos en todo el entorno (sin series por empresa); un catálogo por empresa no vuelve a ser compartido.
- Una referencia de un catálogo compartido a uno por empresa (p. ej. *Existencias* antes de Inventario 1.4.0) muestra la
  etiqueta del registro de otra empresa; el buscador y la validación sí filtran por empresa.
- La conversión se fija al guardar: capturar después un tipo de cambio no recalcula documentos guardados. En un documento
  existente, cambiar la moneda no vuelve a sugerir el tipo (se borra el campo para que se sugiera).
- Tipos de cambio solo por superusuario y sin triangulación; sin consolidación entre empresas.
- Las claves de API trabajan en la empresa de quien las creó (no se cambia desde la UI).
