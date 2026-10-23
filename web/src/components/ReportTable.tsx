import type { ReportResult } from '../api/types';
import { formatReportValue } from './ReportChart';

/** Tabla de un reporte con su total general (también es la alternativa textual de la gráfica). */
export function ReportTable({ result, caption }: { result: ReportResult; caption?: string }) {
  const grouped = result.columns.some((c) => c.kind === 'group');
  const num = (type: string) => (type === 'number' ? 'td-num' : undefined);
  return (
    <div className="table-scroll" tabIndex={0} role="region" aria-label={caption ?? result.label}>
      <table className="table">
        {caption && <caption className="sr-only">{caption}</caption>}
        <thead>
          <tr>{result.columns.map((c) => <th key={c.key} scope="col" className={num(c.type)}>{c.label}</th>)}</tr>
        </thead>
        <tbody>
          {result.rows.map((row, i) => (
            <tr key={i}>
              {result.columns.map((c) => <td key={c.key} className={num(c.type)}>{formatReportValue(c, row[c.key])}</td>)}
            </tr>
          ))}
          {!grouped && (
            <tr>{result.columns.map((c) => <td key={c.key} className={num(c.type)}>{formatReportValue(c, result.totals[c.key])}</td>)}</tr>
          )}
        </tbody>
        {grouped && result.rows.length > 0 && (
          <tfoot>
            <tr className="report-total">
              {result.columns.map((c, i) => (
                <td key={c.key} className={num(c.type)}>{c.kind === 'measure' ? formatReportValue(c, result.totals[c.key]) : i === 0 ? 'Total' : ''}</td>
              ))}
            </tr>
          </tfoot>
        )}
      </table>
    </div>
  );
}
