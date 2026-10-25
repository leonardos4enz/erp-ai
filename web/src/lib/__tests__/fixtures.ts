// Spec 030 (H4): un modelo mínimo para probar el autocompletado y el constructor (Pedido → Cliente → Zona; partidas).
import type { EntityDef, ExprFunction, FieldDef } from '../../api/types';
import type { CompletionScope } from '../exprComplete';

const f = (name: string, type: FieldDef['type'], extra: Partial<FieldDef> = {}): FieldDef => ({ name, label: name, type, ...extra });
const entity = (name: string, fields: FieldDef[]) => ({ name, label: name, labelPlural: name, fields }) as unknown as EntityDef;

export const zona = entity('Zona', [f('Clave', 'text'), f('Nombre', 'text')]);
export const cliente = entity('Cliente', [f('Nombre', 'text'), f('Zona', 'reference', { referenceEntity: 'Zona' }), f('Limite', 'decimal')]);
export const partida = entity('PedidoPartida', [f('Cantidad', 'decimal'), f('Importe', 'decimal')]);
export const pedido = entity('Pedido', [
  f('Clave', 'text'),
  f('Cliente', 'reference', { referenceEntity: 'Cliente' }),
  f('Prioridad', 'choice', { options: ['Alta', "D'Media", 'Baja'] }),
  f('Total', 'decimal'),
  f('Fecha', 'date'),
  f('Urgente', 'boolean'),
  f('Responsable', 'user'),
  f('Estado', 'state'),
  f('Partidas', 'lines', { childEntity: 'PedidoPartida' }),
  f('Viejo', 'text', { isArchived: true }),
]);
const functions = ['IF', 'ISBLANK', 'SUM', 'TODAY', 'ADDDAYS', 'CURRENTUSER']
  .map((name) => ({ name, min: 0, max: name === 'TODAY' ? 0 : 3 })) as unknown as ExprFunction[];

export const scope: CompletionScope = { fields: pedido.fields, states: ['Borrador', 'Enviado'], entities: [pedido, cliente, zona, partida], functions };
