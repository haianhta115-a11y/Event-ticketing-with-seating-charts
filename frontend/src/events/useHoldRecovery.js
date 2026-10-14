import { useCallback, useEffect, useRef, useState } from 'react';
import { getShowtimeSeats, holdSeat, holdSeats } from '../api/seatsApi';
import { getMyHoldSession, releaseSeat } from '../api/seatReservationApi';
import { createHoldRecovery } from './holdRecovery';

export function useHoldRecovery(showId) {
  const controller = useRef(null);
  const [state, setState] = useState({ data: null, session: null, remainingSeconds: 0, loading: true, pending: false, error: null, notice: null });
  useEffect(() => {
    const recovery = createHoldRecovery({ showId, api: { seats: getShowtimeSeats, session: getMyHoldSession, hold: holdSeat, batch: holdSeats, release: releaseSeat },
      readToken: () => localStorage.getItem('token'), onChange: setState });
    controller.current = recovery;
    void recovery.load();
    const timer = setInterval(recovery.tick, 1000);
    const refresh = () => { recovery.tick(); void recovery.load(); };
    const visible = () => { if (document.visibilityState === 'visible') refresh(); };
    window.addEventListener('focus', refresh);
    window.addEventListener('storage', refresh);
    window.addEventListener('sessionchange', refresh);
    document.addEventListener('visibilitychange', visible);
    return () => {
      recovery.dispose(); clearInterval(timer);
      window.removeEventListener('focus', refresh); window.removeEventListener('storage', refresh);
      window.removeEventListener('sessionchange', refresh); document.removeEventListener('visibilitychange', visible);
    };
  }, [showId]);
  return { ...state, refresh: useCallback(() => controller.current?.load(), []),
    dismissNotice: useCallback(() => controller.current?.dismissNotice(), []),
    confirmBatch: useCallback(ids => controller.current?.hold(ids), []),
    confirmRelease: useCallback(id => controller.current?.release(id), []),
    confirmHold: useCallback(id => controller.current?.hold(id), []) };
}
