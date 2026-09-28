export const requestJsonResponse = async (url, options = {}) => {
  const resp = await fetch(url, options);
  const data = await resp.json().catch(() => null);
  return { resp, data };
};

export const requestJson = async (url, options = {}) => {
  const { resp, data } = await requestJsonResponse(url, options);
  if (!resp.ok) {
    const message = data && (data.error || data.message) ? String(data.error || data.message) : `HTTP ${resp.status}`;
    throw new Error(message);
  }
  return data;
};
