/**
 * Checks if the given order status string represents a pending/waiting state
 * @param {string|object} [status]
 * @returns {boolean}
 */
export const isPendingOrderStatus = (status) => {
  const rawStatus = typeof status === 'object' && status !== null ? status.status : status;
  if (!rawStatus || typeof rawStatus !== 'string') return false;
  const normalized = rawStatus.trim().toLowerCase().replace(/[\s-_]/g, '');
  return (
    normalized === 'pending' ||
    normalized === 'waiting' ||
    normalized === 'waitingforpayment' ||
    normalized === 'chothanhtoan'
  );
};

/**
 * Checks if there is at least one failed payment attempt in the order's history/events
 * @param {any} orderData
 * @returns {boolean}
 */
export const hasFailedPaymentAttempt = (orderData) => {
  if (!orderData) return false;
  const data = orderData.order || orderData;

  if (
    data.hasFailedPayment === true ||
    data.hasFailedPayments === true ||
    data.hasFailedPaymentHistory === true ||
    data.has_failed_payments === true ||
    data.has_failed_payment === true ||
    String(data.hasFailedPayments).toLowerCase() === 'true' ||
    String(data.hasFailedPayment).toLowerCase() === 'true'
  ) {
    return true;
  }
  if (typeof data.failedPaymentCount === 'number' && data.failedPaymentCount > 0) {
    return true;
  }
  if (Array.isArray(data.failedPayments) && data.failedPayments.length > 0) {
    return true;
  }
  if (Array.isArray(data.failed_payments) && data.failed_payments.length > 0) {
    return true;
  }

  const collections = [
    data.paymentEvents,
    data.payment_events,
    data.events,
    data.history,
    data.paymentHistory,
    data.payment_history,
    data.paymentAttempts,
    data.payment_attempts,
    data.attempts,
    data.payments,
  ];

  for (const list of collections) {
    if (Array.isArray(list) && list.length > 0) {
      const foundFailed = list.some((item) => {
        if (!item) return false;
        if (typeof item === 'string') {
          const lower = item.toLowerCase();
          return lower.includes('fail') || lower === 'payment_failed';
        }
        if (item.success === false || item.Success === false) return true;
        if (item.isFailed === true || item.failed === true) return true;

        const statusStr = String(item.status || item.outcome || item.state || '').toLowerCase();
        if (['failed', 'failure', 'gatewayfailed', 'error'].includes(statusStr)) return true;

        const typeStr = String(item.type || item.event || item.name || '').toLowerCase();
        if (['paymentfailed', 'payment_failed', 'failed', 'failure'].includes(typeStr)) return true;

        return false;
      });
      if (foundFailed) return true;
    }
  }

  return false;
};

/**
 * Determines whether the Retry Payment button should be displayed
 * MUST ONLY be visible IF AND ONLY IF current order status is Pending (or waiting)
 * AND there is at least one failed payment attempt in the order's history/events.
 * @param {any} orderData
 * @returns {boolean}
 */
export const shouldShowRetryPayment = (orderData) => {
  if (!orderData) return false;
  const data = orderData.order || orderData;
  const isExpired = Boolean(
    data.isExpired ||
    data.status === 'Expired' ||
    (data.expiresAtUtc && new Date(data.expiresAtUtc).getTime() <= (data.serverTimeUtc ? new Date(data.serverTimeUtc).getTime() : Date.now())) ||
    (typeof data.remainingSeconds === 'number' && data.remainingSeconds <= 0 && data.status === 'Pending')
  );
  if (isExpired) return false;
  return isPendingOrderStatus(data?.status) && hasFailedPaymentAttempt(data);
};

