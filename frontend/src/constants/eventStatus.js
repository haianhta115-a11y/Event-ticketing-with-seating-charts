export const EVENT_STATUS = {
  DRAFT: 'Draft',
  PUBLISHED: 'Published',
  ENDED: 'Ended',
};

export const EVENT_STATUS_CONFIG = {
  [EVENT_STATUS.DRAFT]: {
    label: 'Bản nháp',
    style: {
      backgroundColor: '#fef3c7',
      color: '#b45309',
      border: '1px solid #fde68a',
    },
  },
  [EVENT_STATUS.PUBLISHED]: {
    label: 'Đang mở bán',
    style: {
      backgroundColor: '#dcfce7',
      color: '#15803d',
      border: '1px solid #bbf7d0',
    },
  },
  [EVENT_STATUS.ENDED]: {
    label: 'Đã kết thúc',
    style: {
      backgroundColor: '#f3f4f6',
      color: '#4b5563',
      border: '1px solid #e5e7eb',
    },
  },
};

export const getEventStatusInfo = (status) => {
  return (
    EVENT_STATUS_CONFIG[status] || {
      label: status || 'Không xác định',
      style: {
        backgroundColor: '#f1f5f9',
        color: '#334155',
        border: '1px solid #cbd5e1',
      },
    }
  );
};

// ==========================================
// SCRUM-86: HẰNG SỐ TRẠNG THÁI SUẤT DIỄN
// ==========================================
export const SHOWTIME_STATUS = {
  DRAFT: 'Draft',         // Bản nháp
  ON_SALE: 'OnSale',       // Đang bán
  CLOSED: 'Closed',       // Đóng bán
};

export const SHOWTIME_STATUS_CONFIG = {
  [SHOWTIME_STATUS.DRAFT]: {
    label: 'Bản nháp',
    style: {
      backgroundColor: '#fef3c7',
      color: '#b45309',
      border: '1px solid #fde68a',
    },
  },
  [SHOWTIME_STATUS.ON_SALE]: {
    label: 'Đang mở bán',
    style: {
      backgroundColor: '#dcfce7',
      color: '#15803d',
      border: '1px solid #bbf7d0',
    },
  },
  [SHOWTIME_STATUS.CLOSED]: {
    label: 'Đóng bán',
    style: {
      backgroundColor: '#fee2e2',
      color: '#b91c1c',
      border: '1px solid #fca5a5',
    },
  },
};

export const getShowtimeStatusInfo = (status) => {
  return (
    SHOWTIME_STATUS_CONFIG[status] || {
      label: status || 'Bản nháp',
      style: {
        backgroundColor: '#f1f5f9',
        color: '#334155',
        border: '1px solid #cbd5e1',
      },
    }
  );
};