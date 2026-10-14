// Server authority: no storage of hold IDs/deadlines, and no wall-clock fallback.
const utc = value => typeof value === 'string' && /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? Date.parse(value) : NaN;

export function serverClock(serverTimeUtc, sent, received, now) {
  const stamp = utc(serverTimeUtc);
  if (!Number.isFinite(stamp)) throw new Error('Máy chủ chưa cung cấp thời gian hợp lệ.');
  // Midpoint RTT estimate; visibility/focus recovery resamples after suspension.
  const atReceipt = stamp + Math.max(0, received - sent) / 2;
  return () => atReceipt + Math.max(0, now() - received);
}

export function sessionFromServer(raw, showId, clock) {
  if (!raw) return null;
  const deadline = utc(raw.heldUntilUtc);
  // Existing DTO calls the showtime PerformanceId. Do not invent another session model.
  if (Number(raw.performanceId) !== Number(showId) || !Number.isInteger(raw.userId) || raw.userId <= 0 ||
      !Number.isFinite(deadline) || !Array.isArray(raw.heldSeats) || !raw.heldSeats.length ||
      new Set(raw.heldSeats.map(s => s.id)).size !== raw.heldSeats.length ||
      raw.heldSeats.some(s => !Number.isInteger(s.id) || s.id <= 0 || utc(s.heldUntilUtc) !== deadline)) {
    throw new Error('Phiên giữ chỗ trả về không hợp lệ.');
  }
  return deadline > clock() ? { deadline, heldUntilUtc: raw.heldUntilUtc, heldSeats: raw.heldSeats } : null;
}

const clearOwned = data => data && ({ ...data, seats: data.seats.map(s => s.displayStatus === 'HeldByMe'
  ? { ...s, displayStatus: 'HeldByOther', heldUntilUtc: null, canSelect: false } : s) });

export function createHoldRecovery({ showId, api, readToken, now = () => performance.now(), onChange }) {
  let alive = true, epoch = 0, identity = readToken(), clock = null, busy = false;
  let state = { data: null, session: null, remainingSeconds: 0, loading: true, pending: false, error: null, notice: null };
  const publish = patch => { state = { ...state, ...patch }; if (alive) onChange(state); };
  const current = (version, token) => alive && version === epoch && token === readToken();
  const forget = () => publish({ data: clearOwned(state.data), session: null, remainingSeconds: 0 });

  async function load() {
    if (!alive || busy) return;
    const token = readToken(), version = ++epoch;
    if (token !== identity) { identity = token; forget(); }
    publish({ loading: !state.data, error: null });
    try {
      const sent = now();
      let data = await api.seats(showId);
      const received = now();
      if (!current(version, token)) return;
      let sampledClock = serverClock(data.serverTimeUtc, sent, received, now);
      let session = null;
      if (token) {
        const sessionSent = now();
        try {
          const raw = await api.session(showId); // claims identity only; no userId query
          if (!current(version, token)) return;
          sampledClock = serverClock(raw.serverTimeUtc, sessionSent, now(), now);
          session = sessionFromServer(raw, showId, sampledClock);
          if (!session) data = await api.seats(showId); // expired between the two reads
        } catch (error) {
          if (error.response?.status !== 404) throw error;
          data = await api.seats(showId); // authoritative availability, not a local inference
        }
      }
      if (!current(version, token)) return;
      clock = sampledClock;
      if (session) {
        const ids = new Set(session.heldSeats.map(s => s.id));
        if (session.heldSeats.some(held => !data.seats.some(s => s.id === held.id && s.displayStatus !== 'Sold')))
          throw new Error('Session and chart disagree; refresh required');
        // my-hold is the ownership authority. Never recover identities from chart colors.
        data = { ...clearOwned(data), seats: data.seats.map(s => ids.has(s.id) && s.displayStatus !== 'Sold'
          ? { ...s, displayStatus: 'HeldByMe', heldUntilUtc: session.heldUntilUtc, canSelect: false }
          : clearOwned({ seats: [s] }).seats[0]) };
      } else data = clearOwned(data);
      publish({ data, session, remainingSeconds: session ? Math.max(0, Math.ceil((session.deadline - clock()) / 1000)) : 0, loading: false });
    } catch (error) {
      if (!current(version, token)) return;
      forget();
      publish({ loading: false, error: error.response?.status === 401 ? 'Phiên đăng nhập đã hết hạn.'
        : error.response?.status === 403 ? 'Bạn không có quyền khôi phục giữ chỗ.'
          : error.response?.status === 404 ? 'Suất diễn không tồn tại.'
          : 'Không thể xác minh giữ chỗ từ máy chủ. Vui lòng làm mới.' });
    }
  }

  function tick() {
    if (!alive) return;
    if (readToken() !== identity) { ++epoch; identity = readToken(); forget(); void load(); return; }
    if (!state.session || !clock) return;
    const remaining = Math.max(0, Math.ceil((state.session.deadline - clock()) / 1000));
    if (!remaining) { forget(); void load(); }
    else publish({ remainingSeconds: remaining });
  }

  async function hold(seatId) {
    if (!alive || busy) return;
    const token = readToken();
    if (!token) { forget(); publish({ error: 'Cần đăng nhập để giữ ghế.' }); return; }
    busy = true; ++epoch; const version = epoch;
    publish({ pending: true, error: null, notice: null });
    let notice = null, succeeded = false;
    try {
      const result = Array.isArray(seatId) ? await api.batch(showId, seatId) : await api.hold(showId, seatId);
      if (!current(version, token)) return;
      // Never synthesize a ten-minute deadline if the response is incomplete.
      if (!Number.isFinite(utc(result.heldUntilUtc)) || !Number.isFinite(utc(result.serverTimeUtc)))
        throw new Error('Missing authoritative hold timestamps');
      succeeded = result.success !== false;
    } catch (error) {
      if (!current(version, token)) return;
      forget();
      notice = error.response?.data?.message || 'Không thể giữ ghế. Vui lòng làm mới.';
      const ids = error.response?.data?.rejectedSeatIds;
      // Only IDs from this request can be echoed in the UI. No holder details.
      const submitted = Array.isArray(seatId) ? seatId : [seatId];
      const rejected = Array.isArray(ids) ? ids.filter(id => submitted.includes(id)) : [];
      if (rejected.length) notice += ` Ghế bị từ chối: ${rejected.join(', ')}.`;
    } finally {
      busy = false;
      if (alive) publish({ pending: false });
      if (alive && token !== readToken()) void load();
    }
    if (current(version, token)) {
      await load(); // GET only; never renew on recovery
      if (alive && token === readToken()) publish({ notice });
    }
    return succeeded;
  }

  async function release(seatId) {
    if (!alive || busy) return false;
    const token = readToken();
    // UI ownership is only a guard; the DELETE endpoint still enforces claims ownership.
    if (!token || token !== identity || !state.session?.heldSeats.some(s => s.id === seatId)) {
      if (!token || token !== identity) { forget(); void load(); }
      return false;
    }
    busy = true; const version = ++epoch;
    publish({ pending: true, error: null, notice: null });
    let succeeded = false, notice = null;
    try {
      const result = await api.release(showId, seatId); // no client-supplied owner
      if (!current(version, token)) return false;
      if (result.success !== true) throw new Error('Release not confirmed');
      succeeded = true;
    } catch (error) {
      if (!current(version, token)) return false;
      forget(); // do not guess Available when a write response is uncertain
      notice = error.response?.status === 401 ? 'Phiên đăng nhập đã hết hạn.'
        : error.response?.status === 403 ? 'Bạn không có quyền nhả ghế này.'
          : 'Không thể xác nhận nhả ghế. Đã làm mới trạng thái từ máy chủ.';
    } finally {
      busy = false;
      if (alive) publish({ pending: false });
      if (alive && token !== readToken()) { forget(); void load(); }
    }
    if (current(version, token)) {
      await load(); // reread both session and availability; never change/extend its deadline
      if (alive && token === readToken()) publish({ notice });
    }
    return succeeded;
  }

  return { load, tick, hold, release, dismissNotice() { publish({ notice: null }); }, snapshot: () => state, dispose() { alive = false; ++epoch; } };
}
