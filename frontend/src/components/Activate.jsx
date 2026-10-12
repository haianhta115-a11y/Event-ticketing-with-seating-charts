import { readActivationToken, activationRequest, resendMessage as deliveryError } from '../auth/registration';
import { useState, useEffect, useRef } from 'react';
import { Link } from 'react-router-dom';
import axiosClient from '../api/axiosClient';

export default function Activate() {
  const [token] = useState(() => readActivationToken(window.location));
  const pending = useRef(null);
  const [status, setStatus] = useState(token ? 'loading' : 'error');
  const [resendEmail, setResendEmail] = useState('');
  const [resendMessage, setResendMessage] = useState('');
  const [isSending, setIsSending] = useState(false);

  useEffect(() => {
    if (!token) return;
    
    let active = true;
    pending.current = activationRequest(token, (url, data) => axiosClient.post(url, data), pending.current);
    // Remove legacy query tokens and fragments from browser history after capture.
    const query = new URLSearchParams(window.location.search);
    query.delete('token');
    window.history.replaceState(null, '', window.location.pathname + (query.size ? '?' + query : ''));
    pending.current.promise.then(() => { if (active) setStatus('success'); })
      .catch(() => { if (active) setStatus('error'); });
    return () => { active = false; };
  }, [token]);

  const handleResend = async (e) => {
    e.preventDefault();
    setResendMessage('');
    if (!resendEmail || isSending) return;
    setIsSending(true);

    try {
      const response = await axiosClient.post('/auth/resend-activation', { email: resendEmail });
      setResendMessage(response.data.message);
    } catch (error) {
      setResendMessage(deliveryError(error));
    } finally { setIsSending(false); }
  };

  return (
    <div style={{ maxWidth: '400px', margin: '50px auto', padding: '20px', border: '1px solid #ccc' }}>
      <h2>Activate Account</h2>
      
      {status === 'loading' && (
        <div style={{ marginBottom: '15px' }}>Activating your account...</div>
      )}
      
      {status === 'success' && (
        <div>
          <div style={{ color: 'green', marginBottom: '15px' }}>Account activated successfully!</div>
          <Link to="/login">Login</Link>
        </div>
      )}
      
      {status === 'error' && (
        <div>
          <div style={{ color: 'red', marginBottom: '15px' }}>Activation link expired or invalid.</div>
          
          <form onSubmit={handleResend} style={{ marginTop: '20px', borderTop: '1px solid #eee', paddingTop: '15px' }}>
            <div style={{ marginBottom: '15px' }}>
              <label style={{ display: 'block', marginBottom: '5px' }}>Email</label>
              <input 
                type="email" 
                value={resendEmail} 
                onChange={(e) => setResendEmail(e.target.value)} 
                required 
                style={{ width: '100%', padding: '8px' }}
              />
            </div>
            <button type="submit" disabled={isSending} style={{ width: '100%', padding: '10px', backgroundColor: '#007bff', color: '#fff', border: 'none', marginBottom: '10px' }}>
              {isSending ? 'Đang gửi...' : 'Resend Link'}
            </button>
            {resendMessage && <div style={{ color: 'green', marginTop: '10px' }}>{resendMessage}</div>}
          </form>
        </div>
      )}
    </div>
  );
}
