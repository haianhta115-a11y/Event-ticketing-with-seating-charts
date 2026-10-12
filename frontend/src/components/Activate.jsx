import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import axiosClient from '../api/axiosClient';

export default function Activate() {
  const [searchParams] = useSearchParams();
  const token = searchParams.get('token');
  const [status, setStatus] = useState(token ? 'loading' : 'error');
  const [resendEmail, setResendEmail] = useState('');
  const [resendMessage, setResendMessage] = useState('');

  useEffect(() => {
    if (!token) return;
    
    axiosClient.post('/auth/activate', { token })
      .then(() => {
        setStatus('success');
      })
      .catch(() => {
        setStatus('error');
      });
  }, [token]);

  const handleResend = async (e) => {
    e.preventDefault();
    setResendMessage('');
    if (!resendEmail) return;

    try {
      await axiosClient.post('/auth/resend-activation', { email: resendEmail });
      setResendMessage('If the email is registered, a new link has been sent.');
    } catch {
      setResendMessage('If the email is registered, a new link has been sent.');
    }
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
            <button type="submit" style={{ width: '100%', padding: '10px', backgroundColor: '#007bff', color: '#fff', border: 'none', marginBottom: '10px' }}>
              Resend Link
            </button>
            {resendMessage && <div style={{ color: 'green', marginTop: '10px' }}>{resendMessage}</div>}
          </form>
        </div>
      )}
    </div>
  );
}
