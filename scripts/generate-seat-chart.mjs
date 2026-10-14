import fs from 'node:fs';
import process from 'node:process';
import { pathToFileURL } from 'node:url';

export function generateSeatChart(count = 2000, columns = 50) {
  if (!Number.isInteger(count) || count < 1 || count > 20000 || !Number.isInteger(columns) || columns < 1 || columns > 200)
    throw new Error('Use 1–20000 seats and 1–200 columns.');
  return { layoutName: `Synthetic ${count} seats`, totalCapacity: count, seats: Array.from({ length: count }, (_, i) => ({
    row: `R${String(Math.floor(i / columns) + 1).padStart(2, '0')}`,
    section: 'Main',
    seatNumber: String(i % columns + 1), category: 'Standard',
    x: (i % columns) * 40, y: Math.floor(i / columns) * 40,
  })) };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [count, output] = process.argv.slice(2);
  const data = JSON.stringify(generateSeatChart(count === undefined ? 2000 : Number(count)), null, 2) + '\n';
  if (output) fs.writeFileSync(output, data, { flag: 'wx' }); // never overwrite an existing sample
  else process.stdout.write(data);
}
