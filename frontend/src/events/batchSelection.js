export function toggleBatch(ids, seat) {
  if (seat.displayStatus !== 'Available' || seat.canSelect === false) return ids;
  return ids.includes(seat.id) ? ids.filter(id => id !== seat.id) : [...ids, seat.id];
}
export function batchHoldApi(client, showId, seatIds) {
  return client.post(`/showtimes/${showId}/seats/hold`, { seatIds }).then(response => response.data);
}
