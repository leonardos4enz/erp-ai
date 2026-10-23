# Verificación 009 — Entornos: ciclo de vida de metadatos

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 249/249 (+20 en `EnvironmentTests`: ida y vuelta de Compras + Inventario —exportar todo y aplicarlo sobre el mismo modelo da un diff vacío; sobre un entorno vacío crea todo y converge—, exportar solo Inventario deja Compras como dependencia, campo nuevo / cambiado / local archivado, conflictos de tipo y de campo archivado, proceso redefinido y proceso quitado como conflicto, referencias en ciclo, lo que no va en el paquete no se toca, firma HMAC —formato, alteración, otra clave, sin firma—, clave corta rechazada al arrancar, reglas del paquete —roles con administración, permisos ajenos, roles del sistema, datos, ids de módulos, versiones—) |
| E2E | 132/132 en dos corridas limpias seguidas (+11 en `entornos.spec.ts`; la suite corre como producción: `Environments__RequireApproval=true` y una clave de firma nueva por corrida) |
| Visual | Capturas de referencia sin cambios («Entornos» va después de «Integraciones», fuera del área capturada; los catálogos de la prueba van en su propio grupo del menú); no se aprobó ninguna nueva |
| Base real | `ErpAi`: Compras completo (14 catálogos con partidas, tablero, 8 roles) exportado desde la UI y subido de vuelta: **diff vacío** («Sin cambios en el modelo»), aplicado como `compras-ajustes` 1.0.0 sin conjunto de cambios |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 · CA2 · CA4 — elegir catálogos, vista previa (pasos, roles, advertencias), formato de módulo, firma, huella, historial y auditoría | `entornos › H1 CA1 · CA2 · CA4`; unitarias `Exportar_Compras_e_Inventario…`, `Exportar_solo_Inventario…` |
| H1 CA3 — sin registros, usuarios, contraseñas, destino ni secreto de webhooks | `entornos › H1 CA3` |
| H2 CA1 · CA2 · CA4 — subir, verificar firma, diff contra un entorno que divergió (campo cambiado, campo local archivado con advertencia) | `entornos › H2 CA1 · CA2 · CA4 + H4 CA1`; unitarias `Campo_nuevo_campo_cambiado…`, `Lo_que_no_va_en_el_paquete…` |
| H2 CA3 — conflictos bloquean (campo archivado en el destino) | `entornos › H2 CA3`; unitarias `Cambiar_el_tipo…`, `Proceso_cambiado…` |
| H3 CA1 · CA3 — una transacción por el Plan de Cambios, versión registrada, no se repite ni retrocede, historial | `entornos › H3 CA1 · CA3 + H4 CA3`; unitaria `Versiones` |
| H3 CA2 + H4 CA2 — se aplica el diff revisado; la aprobación queda ligada al diff | `entornos › H4 CA2 + H5 CA2` |
| H4 CA1 · CA3 — cuatro ojos (quien sube no aprueba ni aplica), auditoría de subir, aprobar, rechazar y aplicar | `entornos › H2 … + H4 CA1`, `H3 … + H4 CA3`, `H4 CA2 …` |
| H4 CA4 — solo superusuario | `entornos › H4 CA4` |
| H5 CA1 · CA2 — sin administración, permisos solo sobre el paquete, roles nuevos con sus permisos, existentes sin permisos sobre catálogos existentes | `entornos › H4 CA2 + H5 CA2`; unitarias `Roles_con_administracion…`, `Datos_ids_de_modulos…` |
| H5 CA3 — alterado o sin firma: rechazado antes de guardarse, intento auditado | `entornos › H5 CA3`; unitaria `Firma_hmac…` |
| Móvil sin desbordes | `entornos › Entornos en móvil…` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Alta | Exportar solo *Marcas* fallaba: la vista previa compilaba con los roles del paquete, y el compilador también revisa catálogos que no viajan (el webhook de *Productos* usa un rol que no va en el paquete). Bloqueaba exportar un catálogo suelto | Corregido (se compila con los roles del entorno; los citados por el paquete viajan con él); regresión `entornos › H4 CA2 + H5 CA2`, **verificada fallando con el código anterior** |
| F-02 | Baja | (exploratoria, `ErpAi`) «Con sus partidas: OrdenCompraPartida»: nombre técnico en lugar de la etiqueta | Corregido; regresión en `entornos › H1 CA1…`, **verificada fallando con el código anterior** |
| F-03 | Media | (exploratoria) Los permisos de los roles se listaban por nombre técnico (`Almacen (leer)`): quien aprueba debe entender sobre qué catálogo da acceso | Corregido (`PackagePermission.label`); regresión en `entornos › H1 CA1…`, **verificada fallando con el código anterior** |
| F-04 | Baja | (exploratoria, 375 px) Las insignias aplastaban «Este entorno» en dos renglones. La primera prueba (a 390 px) **no fallaba** con el código anterior porque en la suite las insignias son más largas; se reescribió para recorrer de 320 a 760 px | Corregido (`.env-current > .grow` con ancho mínimo); regresión `entornos › Entornos en móvil…`, **verificada fallando con el código anterior (a 460 px)** |

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- *Entornos* muestra «Desarrollo · Aplicación directa · Sin clave de firma» (configuración por defecto).
- Exportar: 11 catálogos de Compras + tablero *Compras* (nombre con «— ñandú»): vista previa válida, 14 catálogos con partidas, 37 operaciones, 8 roles con sus permisos, dependencia «Movimientos de inventario» (las automatizaciones de recepción la crean) y aviso «Sin firma». Aquí se encontraron F-02 y F-03.
- Subir el archivo exportado al mismo entorno: «Sin instalar → 1.0.0», «Sin cambios en el modelo». Aplicado directamente: registra `compras-ajustes` 1.0.0, sin plan ni roles nuevos. Es el único cambio que quedó en `ErpAi` (una fila en `meta.Module`, la exportación, la importación y su auditoría).
- Móvil (375 px) y tema oscuro: tabla con desplazamiento propio, sin desbordes; ahí se encontró F-04. Consola sin errores ni violaciones de CSP.
- No se modificó al usuario `admin`; la aprobación de cuatro ojos se probó con un segundo administrador en `ErpAi_E2E`.

## Riesgo residual
- La firma usa una clave compartida (HMAC): quien tenga la clave de cualquier entorno de la cadena puede firmar. Firmas asimétricas por entorno quedan para otra spec. Sin clave configurada, un entorno acepta paquetes sin firma (lo advierte la UI); producción debe configurarla.
- El paquete manda sobre sus catálogos: un campo agregado en el destino por otro medio (a mano o por una actualización de módulo) se archiva al aplicar. Se advierte en el diff y requiere aprobación, pero no se fusiona.
- Archivar un catálogo completo no viaja; tampoco se revierte un paquete aplicado (se promueve una versión nueva).
- Una vista compartida que filtra por un registro concreto lleva su id y su etiqueta: en otro entorno puede apuntar a otro registro (se advierte al exportar).
- Si se agrega un metadato nuevo al motor, `ModelDiff` debe compararlo; la prueba de ida y vuelta lo detecta si los módulos lo usan.
