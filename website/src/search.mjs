export function searchDocuments(records, query, limit = 10) {
  const normalize = value => value.normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase();
  const terms = normalize(query).trim().split(/\s+/).filter(Boolean).slice(0, 12);
  if (!terms.length) return [];
  return records.map(record => {
    const title = normalize(record.title);
    const heading = normalize(record.heading);
    const body = normalize(record.text);
    if (!terms.every(term => `${title} ${heading} ${body}`.includes(term))) return null;
    const score = terms.reduce((sum, term) => sum + (heading.startsWith(term) ? 12 : 0) +
      (heading.includes(term) ? 7 : 0) + (title.includes(term) ? 5 : 0) + (body.includes(term) ? 1 : 0), 0);
    const start = Math.max(0, body.indexOf(terms[0]) - 45);
    return { ...record, score, excerpt: `${start ? '...' : ''}${record.text.slice(start, start + 170)}${record.text.length > start + 170 ? '...' : ''}` };
  }).filter(Boolean).sort((left, right) => right.score - left.score || left.title.localeCompare(right.title)).slice(0, limit);
}
