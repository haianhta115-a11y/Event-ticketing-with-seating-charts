import { openEvents } from '../events/publicEvents';
import axiosClient from './axiosClient';
import { managementApi } from '../events/management';
import { importChart, previewChart } from '../events/seatImport';
import { salesApi } from '../events/sales';
export const updateEvent = (...args) => managementApi(axiosClient).updateEvent(...args);


export const getEvents = async () => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  return managementApi(axiosClient).getEvents();
};

export const getAdminEvents = async () => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  const response = await axiosClient.get('/events/admin');
  return response.data;
};

export const getEventById = async (id) => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  return managementApi(axiosClient).getEventById(id);
};

export const createEvent = async (data) => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  return managementApi(axiosClient).createEvent(data);
};

export const publishEvent = (...args) => salesApi(axiosClient).publishEvent(...args);
export const unpublishEvent = (...args) => salesApi(axiosClient).unpublishEvent(...args);

export const getShowtimes = async (eventId) => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  return managementApi(axiosClient).getShowtimes(eventId);
};

export const createShowtime = async (eventId, data) => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  return managementApi(axiosClient).createShowtime(eventId, data);
};

export const updateShowtimeSalesStatus = (...args) => salesApi(axiosClient).updateShowtimeSalesStatus(...args);

export const importSeatingChart = async (showtimeId, jsonData) => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');
  const response = await axiosClient.post('/showtimes/' + showtimeId + '/seats/import', jsonData);
  return response.data;
};

export const uploadSeatingChart = (showtimeId, file) => importChart(axiosClient, showtimeId, file);

export const previewSeatingChart = (showtimeId, file) => previewChart(axiosClient, showtimeId, file);

export const getOpenEvents = cursor => openEvents(axiosClient, cursor);

export const getEventSeatPricing = (...args) => managementApi(axiosClient).getEventSeatPricing(...args);
export const updateEventSeatPricing = (...args) => managementApi(axiosClient).updateEventSeatPricing(...args);

