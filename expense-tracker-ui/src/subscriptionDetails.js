export function providerLink(value) {
  if (typeof value !== 'string' || /[\s\u0000-\u001f\u007f\\]/u.test(value)) return null
  try {
    const url = new URL(value)
    return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? { href: url.href, host: url.host } : null
  } catch { return null }
}

export function trialInfo(value) {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null
  const today = new Date().toISOString().slice(0, 10)
  const days = Math.round((Date.parse(`${value}T00:00:00Z`) - Date.parse(`${today}T00:00:00Z`)) / 86400000)
  return Number.isFinite(days) ? { days, label: days < 0 ? 'Probă încheiată' : days === 0 ? 'Proba se încheie azi' : `Probă · încă ${days} zile` } : null
}
