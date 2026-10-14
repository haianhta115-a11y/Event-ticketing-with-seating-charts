import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { categoryPalette, previewGrid } from '../src/events/seatImport.js';
import { seatPresentation, rowLabels, existingPrice, blockedSeatMessage } from '../src/events/seatPresentation.js';

const seat = { id: 7, row: 'A', number: '12', category: 'VIP', displayStatus: 'Available' };
test('Buyer and Organizer share the unchanged category palette including VIP/Standard/Economy', () => {
  const names = ['VIP', 'Standard', 'Economy'];
  const palette = categoryPalette(names);
  assert.deepEqual(palette, previewGrid(names.map(category => ({ ...seat, category }))).categories);
  assert.deepEqual(palette.map(c => c.color), ['hsl(0 65% 75%)', 'hsl(138 65% 75%)', 'hsl(275 65% 75%)']);
  assert.deepEqual(categoryPalette(['VIP', 'Standard', 'VIP']), previewGrid([
    {...seat,category:'VIP'},{...seat,category:'Standard'}]).categories);
});

test('available seats use category colour; queued selection never becomes an authoritative hold', () => {
  const color = 'hsl(138 65% 75%)';
  const available = seatPresentation(seat, color), queued = seatPresentation(seat, color, true);
  assert.equal(available.fill, color); assert.equal(available.selectable, true);
  assert.equal(queued.fill, color); assert.equal(queued.stroke, '#7c3aed');
  assert.equal(queued.icon, '✓'); assert.match(queued.status, /chờ giữ/);
  assert.equal(seat.displayStatus,'Available');
});

test('own and foreign holds have hourglass, different backgrounds and different selection permissions', () => {
  const mine = seatPresentation({...seat,displayStatus:'HeldByMe'},'pink',true);
  const other = seatPresentation({...seat,displayStatus:'HeldByOther'},'pink',true);
  assert.equal(mine.icon,'⏳'); assert.equal(other.icon,'⏳');
  assert.notEqual(mine.fill,other.fill); assert.equal(mine.selectable,true); assert.equal(other.selectable,false);
  assert.equal(mine.queued,false); assert.equal(other.queued,false);
  assert.match(mine.label,/Bạn đang giữ/); assert.match(other.label,/Người khác đang giữ/);
  assert.doesNotMatch(other.label,/userId|owner|email/);
});

test('sold and unknown states are unavailable and cannot inherit pending-selection style', () => {
  const sold=seatPresentation({...seat,displayStatus:'Sold'},'pink',true,true);
  assert.equal(sold.icon,'✕'); assert.equal(sold.fill,'#e5e7eb'); assert.equal(sold.selectable,false);
  assert.equal(sold.queued,false); assert.equal(sold.stroke,'#9ca3af');
  assert.equal(seatPresentation({...seat,displayStatus:'unknown'}).selectable,false);
});

test('disabled-seat feedback matches S-10 wording and refreshes without requesting a hold', async () => {
  assert.match(blockedSeatMessage('HeldByOther'), /ghế vừa có người chọn/);
  assert.match(blockedSeatMessage('Sold'), /đã bán/);
  const source=await readFile(new URL('../src/pages/SeatMap.jsx',import.meta.url),'utf8');
  assert.match(source,/setInteractionNotice\(blockedSeatMessage\(seat.displayStatus\)\);\s+void refresh\(\)/);
});

test('row labels stay in the outer margin and never invent row alignment or overlap inner sections', () => {
  const points=[{...seat,pixelX:40,pixelY:100,size:32},{...seat,number:'13',pixelX:80,pixelY:100,size:32}];
  const labels=rowLabels(points,40); assert.equal(labels.length,1);assert.equal(labels[0].row,'A');assert.equal(labels[0].x,30);
  assert.equal(rowLabels([{...points[0],pixelX:200}],40).length,0);
  assert.equal(rowLabels([points[0],{...points[1],pixelY:140}],40).length,0);
});

test('no invented price; only finite non-negative values actually returned by a contract can display', () => {
  for(const value of [undefined,null,'100',NaN,Infinity,-1])assert.equal(existingPrice(value),null);
  assert.equal(existingPrice(0),'0 đ'); assert.equal(existingPrice(10000),'10.000 đ');
});

async function actualTile() {
  const source=await readFile(new URL('../src/pages/SeatMap.jsx',import.meta.url),'utf8');
  const start=source.indexOf('const SeatItem = React.memo('), end=source.indexOf('\n});',start)+4;
  assert.ok(start>=0&&end>start);
  const fragment=`import React from 'react';\nimport { seatPresentation } from '${new URL('../src/events/seatPresentation.js',import.meta.url).href}';\n${source.slice(start,end)}\nexport default SeatItem;`;
  const transformed=await transformWithOxc(fragment,'ActualSeatTile.jsx',{jsx:{runtime:'automatic',development:false}});
  const code=transformed.code.replace(/(['"])react\/jsx-runtime\1/,JSON.stringify(import.meta.resolve('react/jsx-runtime')))
    .replace(/(['"])react\1/,JSON.stringify(import.meta.resolve('react')));
  return (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
}

test('actual routed tile renders readable numbers, category fill and accessible disabled sold/foreign states', async () => {
  const Tile=await actualTile();
  const render=(status,extra={})=>renderToStaticMarkup(React.createElement(Tile,{seat:{...seat,displayStatus:status},pixelX:40,pixelY:100,size:32,
    categoryColor:'hsl(138 65% 75%)',showLabel:true,...extra}));
  const available=render('Available');assert.match(available,/fill="hsl\(138 65% 75%\)"/);assert.match(available,/data-seat-number="true"[^>]*>12<\/text>/);
  assert.match(available,/data-seat-row="true"[^>]*>A<\/text>/);
  const sold=render('Sold');assert.match(sold,/aria-disabled="true"/);assert.match(sold,/tabindex="-1"/);assert.match(sold,/✕/);
  const other=render('HeldByOther');assert.match(other,/aria-disabled="true"/);assert.match(other,/Người khác đang giữ/);assert.match(other,/⏳/);
  const own=render('HeldByMe');assert.match(own,/aria-disabled="false"/);assert.match(own,/Bạn đang giữ/);
  const pending=render('Available',{isPending:true});assert.match(pending,/data-pending-selection="true"/);assert.match(pending,/stroke-dasharray="3 2"/);
  const overview=render('Available',{showLabel:false});assert.doesNotMatch(overview,/data-seat-number/);
});

test('actual routed tile click and Enter never select sold or another buyer’s held seat', async () => {
  const Tile=await actualTile();
  for(const status of ['Available','HeldByMe','HeldByOther','Sold']) {
    const calls=[];
    const tree=Tile.type({seat:{...seat,displayStatus:status},pixelX:0,pixelY:0,size:32,categoryColor:'pink',onClick:s=>calls.push(s.id)});
    tree.props.onClick({detail:0}); tree.props.onKeyDown({key:'Enter',preventDefault(){}});
    assert.equal(calls.length,['Available','HeldByMe'].includes(status)?2:0);
  }
});
