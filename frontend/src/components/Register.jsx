import { validateRegistration } from '../auth/registration';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import axiosClient from '../api/axiosClient';

export default function Register() {
  const [fullName, setFullName] = useState('');
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [isLoading, setIsLoading] = useState(false);

  const handleRegister = async (e) => {
    e.preventDefault();
    setError('');
    setMessage('');

    const validation = validateRegistration({ email, password, username, fullName });
    if (validation) { setError(validation); return; }

    setIsLoading(true);

    try {
      const response = await axiosClient.post('/auth/register', { email, username, fullName, password });
      setMessage(response.data.message);
      setPassword('');
    } catch (err) {
      if (err.response?.data?.errors) {
          const errorMessages = Object.values(err.response.data.errors).flat().join(', ');
          setError(errorMessages);
        } else if (err.response?.data?.detail) {
        setError(err.response.data.detail);
      } else if (err.response?.data?.message) {
        setError(err.response.data.message);
      } else {
        setError('An error occurred during registration.');
      }
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <div style={{ maxWidth: '400px', margin: '50px auto', padding: '20px', border: '1px solid #ccc' }}>
      <h2>Register</h2>
      {error && <div style={{ color: 'red', marginBottom: '10px' }}>{error}</div>}
      {message && <div style={{ color: 'green', marginBottom: '10px' }}>{message}</div>}
      <form onSubmit={handleRegister}>
        <div style={{ marginBottom: '15px' }}>
          <label style={{ display: 'block', marginBottom: '5px' }}>Full Name</label>
          <input 
            type="text" 
            value={fullName} 
            onChange={(e) => setFullName(e.target.value)} 
            required 
            style={{ width: '100%', padding: '8px' }}
          />
        </div>
        <div style={{ marginBottom: '15px' }}>
          <label style={{ display: 'block', marginBottom: '5px' }}>Username</label>
          <input 
            type="text" 
            value={username} 
            onChange={(e) => setUsername(e.target.value)} 
            required 
            style={{ width: '100%', padding: '8px' }}
          />
        </div>
        <div style={{ marginBottom: '15px' }}>
          <label style={{ display: 'block', marginBottom: '5px' }}>Email</label>
          <input 
            type="email" 
            value={email} 
            onChange={(e) => setEmail(e.target.value)} 
            required 
            style={{ width: '100%', padding: '8px' }}
          />
        </div>
        <div style={{ marginBottom: '15px' }}>
          <label style={{ display: 'block', marginBottom: '5px' }}>Password</label>
          <input 
            type="password"
            minLength={8}
            value={password} 
            onChange={(e) => setPassword(e.target.value)} 
            required 
            style={{ width: '100%', padding: '8px' }}
          />
        </div>
        <button type="submit" disabled={isLoading} style={{ width: '100%', padding: '10px', backgroundColor: '#007bff', color: '#fff', border: 'none', marginBottom: '10px' }}>
          {isLoading ? 'Registering...' : 'Register'}
        </button>
      </form>
      <div style={{ textAlign: 'center' }}>
        <Link to="/login">Back to Login</Link>
        <p><Link to="/activate">Gửi lại liên kết kích hoạt</Link></p>
      </div>
    </div>
  );
}

