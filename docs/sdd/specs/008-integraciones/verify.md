# Verificación 008 — Integraciones: webhooks, claves de API, importación y exportación

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 229/229 (+76 en `IntegrationTests`: política SSRF —IPs internas, mapeadas IPv4/IPv6, metadatos de nube, `localhost`, revisión al conectar—, firma HMAC y reenvío tardío, formato y hash de claves, validación de webhooks —eventos, rol, campos que el rol no ve, revalidación al archivar o cambiar la seguridad—, lectores CSV y XLSX —comillas, `;`, BOM, Latin-1, XXE, zip bomb—, conversión de celdas y mapeo de columnas con seguridad; Compras e Inventario compilan con sus roles de servicio) |
| E2E | 122/122 en dos corridas limpias seguidas (+14 en `integraciones.spec.ts`, con un receptor HTTP local que verifica la firma; `expectAccessible` ahora espera a que terminen las animaciones) |
| Visual | Capturas de referencia sin cambios («Integraciones» va después de «Auditoría», fuera del área capturada); no se aprobó ninguna nueva |
| Actualización real | Base `ErpAi`: Compras **1.4.0 → 1.5.0** e Inventario **1.2.0 → 1.3.0** desde la UI, con vista previa válida (1 paso cada una) y los roles «Integración de almacén» e «Integración de tienda» creados con permisos de solo lectura |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 — webhooks como metadato, validados en la vista previa; modelador los muestra sin destino | `integraciones › H1 CA1`; unitarias `Webhook_*`, `Eventos_invalidos_*`, `Un_webhook_no_puede_pedir_un_campo_que_su_rol_no_ve`, `Archivar_un_campo_*` |
| H1 CA2 — destino por entorno; URLs internas y con credenciales rechazadas; secreto una vez, nunca devuelto ni auditado | `integraciones › H1 CA2`; unitarias `Direcciones_internas_*`, `Urls_no_permitidas`, `El_envio_revisa_la_ip_al_conectar_*` |
| H1 CA3 · CA5 · CA6 — bandeja transaccional (lo revertido no sale), firma, carga con la seguridad del rol (sin campo oculto ni registro fuera de alcance), `fields` | `integraciones › H1 CA3 · CA5 · CA6`; unitaria `Firma_hmac_*` |
| H1 CA4 — reintentos con el mismo id y cuerpo, bitácora, «Reintentar», «Probar», rotación del secreto | `integraciones › H1 CA4` |
| H2 CA1 · CA4 — clave una vez, solo hash, sin roles de administración, cuenta de servicio fuera de Usuarios y del buscador | `integraciones › H2 CA1 · CA4`; unitarias `Secretos_y_claves_*`, `Clave_con_formato_invalido_*` |
| H2 CA2 · CA3 — datos con su rol (006), sin CSRF, 403 fuera de datos y reportes, auditoría a nombre del servicio, no inicia sesión, rotación con y sin gracia, revocación | `integraciones › H2 CA2 · CA3` |
| H3 CA1 · CA2 · CA4 · CA5 — plantilla, columnas reconocidas/ignoradas/no reconocidas (oculto = no reconocido), errores por fila, todo o nada | `integraciones › H3 CA1 · CA2 · CA4 · CA5`, `H3 CA1: en móvil…`; unitarias `Csv_*`, `Xlsx_*`, `Columnas_por_nombre_o_etiqueta_con_seguridad` |
| H3 CA3 · CA5 — motor completo (valores por defecto, fórmulas, referencias), apóstrofo de exportación, auditoría | `integraciones › H3 CA3 · CA5`; unitarias `Si_no_*`, `Fechas_*`, `Numeros_*`, `El_apostrofo_*` |
| H4 CA1 · CA2 — exportar la vista (búsqueda, columnas), sin ocultos ni fuera de alcance, CSV sin inyección, XLSX, reimportable, auditoría | `integraciones › H4 CA1 · CA2` |
| H5 CA1 · CA2 · CA3 — roles en actualizaciones, Compras 1.5.0 sin destino, orden autorizada sin importes | `integraciones › H5 CA1 · CA2 · CA3`, `H5 CA1: la vista previa…`; unitarias `Compras_trae_su_webhook_*`, `Compras_e_Inventario_compilan…`; manual en `ErpAi` |
| Solo superusuario administra integraciones | `integraciones › Integraciones: solo el superusuario…`, `H1 CA2` (403), `H2 CA2` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | La vista previa de «Actualizar Compras» no decía que se crearía el rol «Integración de almacén» (la sección de roles solo existía al instalar) | Corregido en `ModulesPage`; regresión `integraciones › H5 CA1: la vista previa de una actualización…`, **verificada fallando con el código anterior** |
| F-02 | Media | En móvil (390 px) el diálogo de importación medía 449 px: el pie con tres botones y el selector de archivo ensanchaban la columna del *overlay* y «Importar» quedaba cortado | Corregido (`.overlay` con columna `minmax(0, 1fr)`, pie que se acomoda en filas, selector de archivo sin ancho mínimo); regresión `integraciones › H3 CA1: en móvil…`, **verificada fallando con el código anterior** |
| F-03 | Baja | (pruebas) axe medía el contraste de un diálogo a mitad de su animación de entrada y reportaba un falso error | `expectAccessible` espera a que terminen las animaciones; no es un bug del producto |

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- Módulos: las dos vistas previas mostraron un paso («Definir 1 webhook(s)… el destino se configura en Integraciones») y, tras F-01, los roles que se crean. Ambas aplicadas; tarjetas en v1.5.0 y v1.3.0.
- Integraciones: «Orden autorizada» y «Existencia actualizada» aparecen «Sin configurar» con su rol de servicio. Un destino `https://169.254.169.254/latest/meta-data` se rechaza con «La URL apunta a una dirección interna». **No se guardó ningún destino ni clave en `ErpAi`.**
- Listado de proveedores en 390 px: «Exportar» e «Importar» se acomodan sin desbordar; ahí se encontró F-02.
- Claves de API en tema oscuro: estado vacío y nota de uso legibles. Consola sin errores ni violaciones de CSP (solo los 401/400 esperados de la sesión y de la URL rechazada).
- No se modificó al usuario `admin`; los roles y claves se probaron con usuarios propios en `ErpAi_E2E`.

## Riesgo residual
- Entrega «al menos una vez» y sin orden garantizado: el receptor debe deduplicar por `X-ERP-Delivery` y no asumir orden.
- La URL y el secreto dependen de las llaves de Data Protection (en desarrollo, el perfil del usuario con DPAPI). Si se pierden, la entrega queda «Fallida» con «vuelve a configurarlo»; en producción conviene `DataProtection:KeysPath` respaldado.
- La carga es una foto del evento: si una automatización posterior cambia otra vez el registro, llega como otro evento.
- La importación interpreta las fechas-hora sin zona como UTC y los números en formato es-MX (coma de miles, punto decimal).
- Solo altas: actualizar registros por importación y las partidas quedan fuera de alcance.
