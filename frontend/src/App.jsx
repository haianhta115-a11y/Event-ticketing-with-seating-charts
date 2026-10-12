import { Outlet, Link, useNavigate } from 'react-router-dom'
import RequireRole from './components/RequireRole'
import './App.css'

function App() {
  const navigate = useNavigate();
  const token = localStorage.getItem('token');
  const role = localStorage.getItem('role');

  const handleLogout = () => {
    localStorage.removeItem('token');
    localStorage.removeItem('role');
    navigate('/login');
  };

  return (
    <div className="App">
      <header style={{ padding: '10px', borderBottom: '1px solid #ccc', display: 'flex', gap: '15px', alignItems: 'center' }}>
        <Link to="/">Home</Link>
        <RequireRole allowedRoles={['Admin', 'Organizer']}>
          <Link to="/events">Sự kiện</Link>
        </RequireRole>
        {!token ? (
          <Link to="/login">Login</Link>
        ) : (
          <button onClick={handleLogout} style={{ background: 'none', border: 'none', color: 'blue', cursor: 'pointer', textDecoration: 'underline' }}>Logout</button>
        )}
      </header>

      <main style={{ padding: '20px' }}>
        {token && (
          <div style={{ marginBottom: '20px', padding: '10px', backgroundColor: '#eef' }}>
            <p>Welcome! Your role is: {role}</p>
          </div>
        )}
        
        <RequireRole allowedRoles={['Admin', 'Accountant']}>
          <div style={{ padding: '10px', backgroundColor: '#fee', border: '1px solid red' }}>
            <h3>Đối soát kế toán (Accounting Hub)</h3>
            <p>This menu is only visible to Admin and Accountant roles.</p>
          </div>
        </RequireRole>

        <Outlet />
      </main>
    </div>
  )
}

export default App
