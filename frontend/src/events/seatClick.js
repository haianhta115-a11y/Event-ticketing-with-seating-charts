// Single mode fulfils click-to-hold; explicit multi mode submits one atomic batch.
// Pending picks are never presented as authoritative holds.
export function seatClick({ seat, batchMode, pending, focus, hold, release, toggle }) {
  if (pending) return;
  if (seat.displayStatus === 'HeldByMe') return release(seat.id);
  if (seat.displayStatus !== 'Available' || seat.canSelect === false) return;
  focus(seat);
  return batchMode ? toggle(seat) : hold(seat.id);
}
