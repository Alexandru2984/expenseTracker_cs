'use strict';
module.exports = pathname => {
 if (pathname === '/api/account/notifications') return { enabled: false, available: false, canEnable: true,
  recipient: 'fixture@example.invalid', recipientContext: 'B'.repeat(64), version: '00000000-0000-0000-0000-000000000000',
  timeZone: 'Europe/Bucharest', sendHour: 9, leadDays: 7, renewals: true, trials: true, goalDeadlines: false,
  includeNames: false, nextDigestAtUtc: null, consentRecordedAt: null };
 if (pathname === '/api/account/notifications/time-zones') return ['UTC','Europe/Bucharest','America/New_York'];
 if (pathname === '/api/account/notifications/history') return [];
 return {};
};
