// Finding #5: catalog-explorer filter — lowercasing on every keystroke vs a precomputed index.
//
//   node benchmarks/filter-bench.mjs
//
// Models a wide catalog (the product's stated target) and one keystroke = one full filter pass. The
// "before" arm lowercases every table and column name inside the filter, as the original computed
// did; the "after" arm reuses a lowercased index built once when the schema loads.

const SCHEMAS = 8;
const TABLES_PER_SCHEMA = 250;
const COLUMNS_PER_TABLE = 40;

function buildCatalog() {
  const schemas = [];
  for (let s = 0; s < SCHEMAS; s++) {
    const tables = [];
    for (let t = 0; t < TABLES_PER_SCHEMA; t++) {
      const columns = [];
      for (let c = 0; c < COLUMNS_PER_TABLE; c++) {
        columns.push({ name: `Column_${s}_${t}_${c}` });
      }
      tables.push({ name: `Table_${s}_${t}`, columns });
    }
    schemas.push({ name: `Schema_${s}`, tables });
  }
  return schemas;
}

// ---- before: lowercase inside the filter, every keystroke ----
function filterBefore(schemas, term) {
  return schemas
    .map((schema) => ({
      ...schema,
      tables: schema.tables.filter(
        (table) =>
          table.name.toLowerCase().includes(term) ||
          table.columns.some((column) => column.name.toLowerCase().includes(term)),
      ),
    }))
    .filter((schema) => schema.tables.length > 0);
}

// ---- after: precomputed lowercased index, reused across keystrokes ----
function buildIndex(schemas) {
  return schemas.map((schema) => ({
    schema,
    tables: schema.tables.map((table) => ({
      table,
      nameLower: table.name.toLowerCase(),
      columnsLower: table.columns.map((column) => column.name.toLowerCase()),
    })),
  }));
}

function filterAfter(index, term) {
  const narrowed = [];
  for (const entry of index) {
    const tables = entry.tables
      .filter((t) => t.nameLower.includes(term) || t.columnsLower.some((c) => c.includes(term)))
      .map((t) => t.table);
    if (tables.length > 0) narrowed.push({ ...entry.schema, tables });
  }
  return narrowed;
}

function measure(label, fn, iterations) {
  for (let i = 0; i < 200; i++) fn(); // warm up
  const trials = [];
  for (let trial = 0; trial < 9; trial++) {
    const start = performance.now();
    for (let i = 0; i < iterations; i++) fn();
    trials.push((performance.now() - start) / iterations);
  }
  trials.sort((a, b) => a - b);
  return trials[Math.floor(trials.length / 2)]; // median ms per keystroke
}

const schemas = buildCatalog();
const cells = SCHEMAS * TABLES_PER_SCHEMA * (COLUMNS_PER_TABLE + 1);
const terms = ['col', 'table_3_1', 'zzz_no_match', '7'];
const iterations = 400;

console.log('# #5 catalog-explorer filter — before / after');
console.log(
  `_Catalog: ${SCHEMAS} schemas × ${TABLES_PER_SCHEMA} tables × ${COLUMNS_PER_TABLE} cols ` +
    `(~${cells.toLocaleString()} searchable names) · Node ${process.version}_\n`,
);

// The index is built once per schema load; amortised across a realistic typing burst it is ~free,
// so the keystroke comparison below is the fair one.
const index = buildIndex(schemas);

console.log('| term | before (ms/keystroke) | after (ms/keystroke) | speedup |');
console.log('|---|--:|--:|--:|');
for (const term of terms) {
  const before = measure(`before:${term}`, () => filterBefore(schemas, term), iterations);
  const after = measure(`after:${term}`, () => filterAfter(index, term), iterations);
  console.log(`| \`${term}\` | ${before.toFixed(3)} | ${after.toFixed(3)} | ${(before / after).toFixed(2)}× |`);
}
