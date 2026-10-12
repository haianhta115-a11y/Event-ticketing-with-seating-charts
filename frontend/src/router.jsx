import { createBrowserRouter } from 'react-router-dom';
import App from './App.jsx';
import Login from './components/Login.jsx';
import Register from './components/Register.jsx';
import Activate from './components/Activate.jsx';
import EventList from './components/EventList.jsx';
import CreateEvent from './components/CreateEvent.jsx';
import EventDetail from './components/EventDetail.jsx';
import RequireRoleRoute from './components/RequireRoleRoute.jsx';

import Home from './pages/Home.jsx';
import ShowtimeDetail from './pages/ShowtimeDetail.jsx';
import SeatMap from './pages/SeatMap.jsx';

export const router = createBrowserRouter([
  {
    path: '/',
    element: <App />,
    children: [
      {
        index: true,
        element: <Home />,
      },
      {
        path: 'shows/:id',
        element: <ShowtimeDetail />,
      },
      {
        path: 'shows/:id/seats',
        element: <SeatMap />,
      },
      {
        path: 'login',
        element: <Login />,
      },
      {
        path: 'register',
        element: <Register />,
      },
      {
        path: 'activate',
        element: <Activate />,
      },
      {
        path: 'events',
        element: (
          <RequireRoleRoute allowedRoles={['Admin', 'Organizer']}>
            <EventList />
          </RequireRoleRoute>
        ),
      },
      {
        path: 'events/create',
        element: (
          <RequireRoleRoute allowedRoles={['Admin', 'Organizer']}>
            <CreateEvent />
          </RequireRoleRoute>
        ),
      },
      {
        path: 'events/:id',
        element: (
          <RequireRoleRoute allowedRoles={['Admin', 'Organizer']}>
            <EventDetail />
          </RequireRoleRoute>
        ),
      },
    ],
  },
]);
