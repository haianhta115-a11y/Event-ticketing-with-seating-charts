// Public browsing uses the server-filtered sales feed, never Organizer management data.
export async function openEvents(client) {
  const response = await client.get('/events/open');
  return response.data;
}
