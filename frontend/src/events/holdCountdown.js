// Keeps the existing server-authoritative deadline math outside React rendering.
export function startHoldCountdown({ expiresAtUtc, serverTimeOffsetMs, onTick, onExpired }, clock = {
  now: Date.now, setInterval: globalThis.setInterval, clearInterval: globalThis.clearInterval,
}) {
  let stopped = false;
  let interval;
  const stop = () => {
    if (stopped) return;
    stopped = true;
    clock.clearInterval(interval);
  };
  interval = clock.setInterval(() => {
    if (stopped) return;
    const remaining = Math.max(0, Math.floor((new Date(expiresAtUtc).getTime() - (clock.now() + serverTimeOffsetMs)) / 1000));
    onTick(remaining);
    if (remaining <= 0) { stop(); onExpired(); }
  }, 1000);
  return stop;
}
