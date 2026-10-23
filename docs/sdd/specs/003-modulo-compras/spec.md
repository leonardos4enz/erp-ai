# Spec 003 — Módulo de Compras

**Estado:** En implementación · **Depende de:** spec 002

El módulo se entrega como **paquete de metadatos** (`modules/compras.json`). No contiene
código: es un Plan de Cambios más roles, permisos y datos iniciales. Se instala desde
*Configuración › Módulos*.

## Catálogos

**Catálogos pequeños** (con datos iniciales):
| Catálogo | Campos principales | Datos iniciales |
|---|---|---|
| Moneda | Clave (única), Nombre, Símbolo | MXN, USD, EUR |
| Unidad de medida | Clave (única), Nombre | PZA, KG, LT, M, CAJA, SERV |
| Condición de pago | Nombre, Días de crédito | Contado, 15, 30, 60 días |
| Categoría de producto | Nombre | Materia prima, Empaque, Refacciones, Oficina, Servicios |
| Departamento | Nombre | Producción, Almacén, Administración, Mantenimiento |
| Almacén | Clave, Nombre, Dirección | ALM-01 General |

**Catálogos grandes:**
| Catálogo | Campos principales |
|---|---|
| Proveedor | Folio `PRV-`, RFC (único), razón social, nombre comercial, contacto, correo, teléfono, condición de pago, moneda, límite de crédito, categoría, activo, notas |
| Producto | Clave (única), descripción, categoría, unidad, costo último, IVA %, proveedor preferido, stock mínimo, activo |

## Documentos y procesos

### Requisición de compra (`REQ-000001`)
Encabezado: solicitante (usuario), departamento, fecha, fecha requerida, prioridad, justificación.
Partidas: producto, descripción, cantidad, unidad, costo estimado, importe (`Cantidad * CostoEstimado`).
Total estimado = `SUM(Partidas.Importe)`.

```
Borrador ──enviar──▶ Por aprobar ──aprobar──▶ Aprobada ──convertir──▶ Atendida
   ▲                     │
   └──── devolver ───────┤ (comentario obligatorio)
                         └──rechazar──▶ Rechazada (motivo obligatorio)
```
- Enviar requiere al menos una partida.
- Aprueba "Gerente de compras". Nadie aprueba su propia requisición.

### Orden de compra (`OC-000001`)
Encabezado: proveedor, requisición origen (opcional), fecha, fecha de entrega, almacén destino,
moneda, tipo de cambio, condición de pago, comprador, notas.
Partidas: producto, descripción, cantidad, unidad, precio unitario (sugerido: costo último del
producto), descuento %, IVA % (sugerido: IVA del producto), y calculados: importe, descuento, IVA, total.
Totales: subtotal, descuento, IVA y total de la orden; total en MXN = `Total * TipoCambio`.

```
Borrador ─enviar─▶ Por autorizar ─autorizar─▶ Autorizada ─enviar a proveedor─▶ Enviada
   ▲                    │                                                         │
   └──── devolver ──────┤                                       recibir parcial / recibir
                        └─rechazar─▶ Rechazada                                    ▼
                                                     Recibida parcial ──recibir──▶ Recibida ─cerrar─▶ Cerrada
Cancelar: desde Borrador, Por autorizar, Autorizada o Enviada (motivo obligatorio)
```
**Reglas de negocio:**
1. La fecha de entrega no puede ser anterior a la fecha de la orden.
2. El proveedor debe estar activo.
3. Cantidad > 0, precio ≥ 0, descuento entre 0 y 100, IVA entre 0 y 100.
4. Tipo de cambio > 0; si la moneda es MXN, debe ser 1.
5. No se envía a autorizar sin partidas.
6. **Montos:** hasta $50,000 MXN autoriza el Gerente de compras; arriba de eso, solo el Director.
7. **Control interno:** quien crea la orden no puede autorizarla (salvo el Director).
8. Al autorizar se registran quién autorizó y cuándo. Al cancelar o rechazar, el motivo es obligatorio.
9. Fuera de Borrador la orden no se puede editar.

### Recepción de mercancía (`REC-000001`)
Encabezado: orden de compra, almacén, fecha, factura del proveedor, recibió, notas.
Partidas: producto, cantidad recibida, observaciones.
`Borrador ─confirmar─▶ Confirmada` / `─cancelar─▶ Cancelada`.
- Solo se confirma si la orden está Enviada o Recibida parcial.
- Requiere al menos una partida y número de factura.

## Roles
| Rol | Puede |
|---|---|
| Solicitante | Crear y enviar requisiciones; consultar catálogos |
| Comprador | Proveedores, productos y órdenes (crear, enviar, enviar a proveedor, cancelar) |
| Gerente de compras | Todo lo del comprador; aprobar requisiciones; autorizar OC ≤ $50,000 |
| Director | Autorizar cualquier OC, incluidas las propias |
| Almacenista | Recepciones; marcar órdenes como recibidas |

## Criterios de aceptación
- CA1: El módulo se instala sobre una base existente sin afectar otros catálogos.
- CA2: El flujo completo requisición → OC → autorización → envío → recepción funciona con los roles reales.
- CA3: Cada regla 1–9 está cubierta por al menos una prueba E2E.
